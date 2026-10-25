using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceRunPreparationTests
{
    [Fact]
    public async Task BrokeringAndLaunchOverlapAndLaunchFailureClosesThePreparedRun()
    {
        using var fixture = new Fixture();
        var brokerEntered = Signal();
        var launchEntered = Signal();
        var releaseBroker = Signal();
        var releaseLaunch = Signal();
        fixture.Gateway.Prepare = async token =>
        {
            brokerEntered.SetResult();
            await releaseBroker.Task.WaitAsync(token);
            return fixture.Preparation;
        };
        fixture.Launcher.Launch = async token =>
        {
            launchEntered.SetResult();
            await releaseLaunch.Task.WaitAsync(token);
            return WorkspaceTestTargetLaunchResult.Failure("Launch failed.");
        };

        var run = fixture.RunAsync();
        await Task.WhenAll(brokerEntered.Task, launchEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        releaseBroker.SetResult();
        Assert.False(run.IsCompleted);
        releaseLaunch.SetResult();
        var result = await run;

        Assert.False(result.IsSuccess);
        Assert.Equal("Launch failed.", result.Message);
        var completion = Assert.Single(fixture.Gateway.Completions);
        Assert.Equal(fixture.Preparation.TrackingRunId, completion.RunId);
        Assert.Equal("failed", completion.Status);
        Assert.Equal(0, completion.ModelPassCount);
        Assert.Equal(1, fixture.Gateway.PrepareCount);
    }

    [Fact]
    public async Task SessionTimeoutStopsTheAppAndClosesThePreparedRun()
    {
        using var fixture = new Fixture();
        fixture.Launcher.Launch = _ => Task.FromResult(WorkspaceTestTargetLaunchResult.Success(fixture.Target));

        var result = await fixture.RunAsync();

        Assert.Contains("Timed out", result.Message);
        Assert.Equal(1, fixture.Launcher.StopCount);
        Assert.Equal("failed", Assert.Single(fixture.Gateway.Completions).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchFailureOrExceptionCancelsAndObservesPendingBrokering(bool throws)
    {
        using var fixture = new Fixture();
        var brokerCancelled = false;
        fixture.Gateway.Prepare = async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { brokerCancelled = token.IsCancellationRequested; }
            return fixture.Preparation;
        };
        fixture.Launcher.Launch = _ => throws
            ? throw new IOException("Launch failed.")
            : Task.FromResult(WorkspaceTestTargetLaunchResult.Failure("Launch failed."));

        if (throws) await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        else Assert.False((await fixture.RunAsync()).IsSuccess);

        Assert.True(brokerCancelled);
        Assert.Empty(fixture.Gateway.Completions);
    }

    [Fact]
    public async Task CancellationDuringLaunchClosesAnAlreadyPreparedRunAsCancelled()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var launchEntered = Signal();
        fixture.Launcher.Launch = async token =>
        {
            launchEntered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return WorkspaceTestTargetLaunchResult.Success(fixture.Target);
        };
        var run = fixture.RunAsync(cancellation.Token);
        await launchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("cancelled", Assert.Single(fixture.Gateway.Completions).Status);
    }

    [Fact]
    public async Task NormalCompletionIsNotSubmittedAgainOnDisposal()
    {
        using var fixture = new Fixture();
        await using (var scope = new WorkspaceRunPreparationScope(fixture.Gateway, CancellationToken.None,
            message => Assert.Fail(message)))
        {
            var first = scope.PrepareAsync(_ => Task.FromResult(fixture.Preparation));
            var second = scope.PrepareAsync(_ => throw new InvalidOperationException("Prepared twice."));
            Assert.Same(first, second);
            await first;
            scope.MarkCompletionHandled();
        }
        Assert.Empty(fixture.Gateway.Completions);
    }

    [Fact]
    public async Task CleanupFailurePreservesTheLaunchFailure()
    {
        using var fixture = new Fixture();
        fixture.Gateway.CompletionError = new IOException("Metering unavailable.");
        fixture.Launcher.Launch = _ => Task.FromResult(WorkspaceTestTargetLaunchResult.Failure("Launch failed."));

        var result = await fixture.RunAsync();

        Assert.Equal("Launch failed.", result.Message);
        Assert.Single(fixture.Gateway.Completions);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        private readonly RuntimeCoordinator runtime;
        public Launcher Launcher { get; } = new();
        public Gateway Gateway { get; } = new();
        public WorkspaceTestTarget Target { get; } = new(
            DevicePlatforms.Ios, "sim", "Phone", "test.app", false, false, true);
        public WorkspaceTestRunPreparation Preparation { get; } = WorkspaceTestRunPreparation.WithTransport(
            new UnusedTransport(), Guid.NewGuid(), 64, Guid.NewGuid(), "Team");

        public Fixture()
        {
            runtime = environment.CreateRuntime();
            var tests = Path.Combine(environment.RootPath, "ansight", "tests");
            Directory.CreateDirectory(tests);
            File.WriteAllText(Path.Combine(tests, "example.json"),
                """{"appId":"test.app","prompt":"Inspect the screen."}""");
            Gateway.Prepare = _ => Task.FromResult(Preparation);
        }

        public Task<WorkspaceTestRunResult> RunAsync(CancellationToken token = default)
            => new WorkspaceTestService(runtime, runtime.SimulatorAgent, Launcher, Gateway).RunAsync(
                new WorkspaceTestRunRequest(environment.RootPath, "example",
                    Target: new WorkspaceTestTargetRequest(DeviceIdentifier: Target.DeviceIdentifier),
                    SessionWaitTimeout: TimeSpan.Zero, EnableWorkspaceTools: false), cancellationToken: token);

        public void Dispose() { runtime.Dispose(); environment.Dispose(); }
    }

    private sealed class Launcher : IWorkspaceTestTargetLauncher
    {
        public Func<CancellationToken, Task<WorkspaceTestTargetLaunchResult>> Launch { get; set; } = null!;
        public int StopCount { get; private set; }
        public Task<WorkspaceTestTargetLaunchResult> LaunchAsync(string appId, WorkspaceTestTargetRequest? request,
            IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken) => Launch(cancellationToken);
        public Task<DeviceOperationResult> StopAsync(WorkspaceTestTarget target, CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.FromResult(DeviceOperationResult.Success("terminate-app", target.Platform, target.DeviceIdentifier, "Stopped."));
        }
    }

    private sealed class Gateway : IWorkspaceTestRunGateway
    {
        public Func<CancellationToken, Task<WorkspaceTestRunPreparation>> Prepare { get; set; } = null!;
        public List<WorkspaceTestRunMeterCompletion> Completions { get; } = [];
        public Exception? CompletionError { get; set; }
        public int PrepareCount { get; private set; }
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Prepare(cancellationToken);
        }
        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            Completions.Add(completion);
            if (CompletionError is not null) throw CompletionError;
            return Task.FromResult(WorkspaceTestRunMeterResult.Success());
        }
    }

    private sealed class UnusedTransport : IModelExecutionTransport
    {
        public string Description => "Unused";
        public bool SupportsWebSockets => false;
        public ValueTask<string> ResolveAccessKeyAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new NotSupportedException();
        public string DescribeFailure(int statusCode, string responseContent) => throw new NotSupportedException();
    }
}
