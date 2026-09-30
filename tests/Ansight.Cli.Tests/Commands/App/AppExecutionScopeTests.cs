namespace Ansight.Cli.Tests.Commands.App;

public sealed class AppExecutionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupIsOptInAndClosesOnlyTheSelectedAppOnce(bool closeAppOnCompletion)
    {
        var calls = new List<WorkspaceTestTarget>();
        await using var scope = new AppExecutionScope(
            closeAppOnCompletion,
            (platform, device, app, token) =>
            {
                Assert.False(token.IsCancellationRequested);
                calls.Add(Target(platform, device, app));
                return Task.FromResult(Success());
            },
            Output());
        scope.SetTarget(Target());

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        if (closeAppOnCompletion)
        {
            Assert.Equal(Target(), Assert.Single(calls));
        }
        else
        {
            Assert.Empty(calls);
        }
    }

    [Fact]
    public async Task NoResolvedTargetDoesNotCloseAnything()
    {
        var callCount = 0;
        await using var scope = new AppExecutionScope(true, (_, _, _, _) =>
        {
            callCount++;
            return Task.FromResult(Success());
        }, Output());

        await scope.DisposeAsync();

        Assert.Equal(0, callCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationStillCloseTheAppAndPreserveTheOriginalError(bool cancel)
    {
        using var executionCancellation = new CancellationTokenSource();
        var closed = false;
        var expected = cancel
            ? (Exception)new OperationCanceledException(executionCancellation.Token)
            : new InvalidOperationException("Execution failed.");

        var actual = await Record.ExceptionAsync(async () =>
        {
            await using var scope = new AppExecutionScope(true, (_, _, _, token) =>
            {
                Assert.False(token.IsCancellationRequested);
                Assert.NotEqual(executionCancellation.Token, token);
                closed = true;
                return Task.FromResult(Success());
            }, Output());
            scope.SetTarget(Target());
            executionCancellation.Cancel();
            throw expected;
        });

        Assert.Same(expected, actual);
        Assert.True(closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureIsReportedWithoutThrowing(bool throws)
    {
        using var error = new StringWriter();
        await using var scope = new AppExecutionScope(true, (_, _, _, _) => throws
            ? throw new IOException("Device disconnected.")
            : Task.FromResult(DeviceOperationResult.Failure(
                "terminate-app", "ios", "DEVICE-1", "Device disconnected.")), Output(error));
        scope.SetTarget(Target());

        await scope.DisposeAsync();

        Assert.Contains("[app.close.warning]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Device disconnected.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupHasABoundedTimeoutEvenIfTheDriverIgnoresCancellation()
    {
        using var error = new StringWriter();
        var termination = new TaskCompletionSource<DeviceOperationResult>();
        await using var scope = new AppExecutionScope(
            true, (_, _, _, _) => termination.Task, Output(error), TimeSpan.FromMilliseconds(10));
        scope.SetTarget(Target());

        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        termination.SetResult(Success());

        Assert.Contains("timed out", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionClaimIsReleasedOnlyAfterCleanupFinishes()
    {
        var termination = new TaskCompletionSource<DeviceOperationResult>();
        var claim = new RecordingClaim();
        await using var scope = new AppExecutionScope(true, (_, _, _, _) => termination.Task, Output())
        {
            SessionClaim = claim
        };
        scope.SetTarget(Target());

        var completion = scope.DisposeAsync().AsTask();
        Assert.Equal(0, claim.DisposeCount);
        termination.SetResult(Success());
        await completion;
        await scope.DisposeAsync();

        Assert.Equal(1, claim.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentExecutionsKeepTheirCleanupTargetsSeparate()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(new[] { "DEVICE-1", "DEVICE-2" }.Select(async device =>
        {
            await using var scope = new AppExecutionScope(true, (_, identifier, _, _) =>
            {
                calls.Add(identifier);
                return Task.FromResult(Success());
            }, Output());
            scope.SetTarget(Target(device: device));
            await Task.Yield();
        }));

        Assert.Equal(["DEVICE-1", "DEVICE-2"], calls.Order());
    }

    [Theory]
    [InlineData("ios", "DEVICE-1", null)]
    [InlineData("android", "DEVICE-1", "DEVICE-1")]
    [InlineData("ios", null, "DEVICE-1")]
    [InlineData("android", "DEVICE-1", "Pixel")]
    public void ExistingSessionResolvesItsExactAppAndNativeDevice(
        string platform, string? nativeId, string? explicitId)
    {
        var target = AppExecutionScope.ResolveSessionTarget(
            Snapshot(nativeId), explicitId, Inventory(platform));

        Assert.Equal(platform, target.Platform);
        Assert.Equal("DEVICE-1", target.DeviceIdentifier);
        Assert.Equal("com.example.app", target.ApplicationIdentifier);
        Assert.False(target.ApplicationLaunched);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("unknown", null)]
    [InlineData("DEVICE-1", "DEVICE-2")]
    [InlineData("unknown", "DEVICE-1")]
    public void ExistingSessionNeverGuessesOrClosesAnotherDevice(string? nativeId, string? explicitId)
    {
        Assert.Throws<CliUsageException>(() => AppExecutionScope.ResolveSessionTarget(
            Snapshot(nativeId), explicitId, Inventory("ios")));
    }

    [Fact]
    public void FlagIsBooleanAndLeavesSessionPositionalsIntact()
    {
        var arguments = CliArguments.Parse(
            ["app", "execute", "--close-app-on-completion", "session-1", "--prompt", "Verify home"]);

        Assert.True(arguments.HasFlag("close-app-on-completion"));
        Assert.Null(arguments.GetOption("close-app-on-completion"));
        Assert.Equal("session-1", AppExecutionService.ResolveTarget(arguments).SessionId);
        Assert.False(CliArguments.Parse(["app", "execute", "session-1"]).HasFlag("close-app-on-completion"));
    }

    [Fact]
    public async Task InlineCompatibilityDoesNotSilentlyIgnoreTheFlag()
    {
        var exception = await Assert.ThrowsAsync<CliUsageException>(() =>
            AppExecutionService.RunInlineCompatibilityAsync(
                CliArguments.Parse(["test", "run-inline", "--close-app-on-completion"]),
                Output(), CancellationToken.None));

        Assert.Contains("ansight app execute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpAdvertisesCleanupAndForwardingPreservesTheFlag()
    {
        using var help = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["app", "execute", "--help"]),
            new CliOutput(false, help, TextWriter.Null), CancellationToken.None,
            allowResidentHostForwarding: false, accessAuthorizer: TestAccessAuthorizer.Allow);
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("--close-app-on-completion", help.ToString(), StringComparison.Ordinal);

        var forwarded = false;
        exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["app", "execute", "session-1", "--prompt", "Verify home", "--close-app-on-completion"]),
            Output(), CancellationToken.None, allowResidentHostForwarding: true,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            residentHostForwarder: (arguments, _, _) =>
            {
                Assert.True(CliArguments.Parse(arguments.OriginalArguments).HasFlag("close-app-on-completion"));
                forwarded = true;
                return Task.FromResult<int?>(CliExitCodes.Success);
            });
        Assert.True(forwarded);
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    private static CliOutput Output(TextWriter? error = null) => new(false, TextWriter.Null, error ?? TextWriter.Null);

    private static DeviceOperationResult Success() => DeviceOperationResult.Success("terminate-app", "ios", "DEVICE-1", "Closed.");

    private static WorkspaceTestTarget Target(string platform = "ios", string device = "DEVICE-1", string app = "com.example.app")
        => new(platform, device, "Pixel", app, false, false, true);

    private static DeviceInventory Inventory(string platform)
        => new([], [
            new("DEVICE-1", "Pixel", platform, "runtime", "booted", true, true, platform == "ios" ? "simulator" : "emulator"),
            new("DEVICE-2", "Other", platform, "runtime", "booted", true, true, platform == "ios" ? "simulator" : "emulator")
        ], []);

    private static AppSessionSnapshot Snapshot(string? nativeId)
        => new()
        {
            SessionId = "session-1", AppId = "com.example.app", ClientName = "Example", RemoteAddress = "localhost",
            CreatedUtc = DateTimeOffset.UtcNow, ConfigId = null, Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow, IsHistorical = false, MetricChannels = [], Metrics = [],
            DeviceProfileJson = nativeId is null ? null : $$$"""{"device":{"nativeDeviceId":"{{{nativeId}}}"}}"""
        };

    private sealed class RecordingClaim : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
