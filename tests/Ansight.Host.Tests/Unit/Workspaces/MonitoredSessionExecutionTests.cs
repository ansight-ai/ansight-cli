using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces.Cloud;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class MonitoredSessionExecutionTests
{
    [Fact]
    public void ExecutionReservationIsExclusiveAndDoesNotOwnCaptureLifetime()
    {
        using var fixture = new Fixture();
        var service = fixture.Runtime.WorkspaceTests;
        using (service.ReserveSession(fixture.Session))
            Assert.Contains("already reserved", Assert.Throws<InvalidOperationException>(
                () => service.ReserveSession(fixture.Session)).Message);
        Assert.True(fixture.Runtime.IsSessionLive(fixture.Session.SessionId));
        using (service.ReserveSession(fixture.Session)) { }
        fixture.State.EndDeviceSession(fixture.Session.SessionId);
        Assert.Contains("no longer live", Assert.Throws<InvalidOperationException>(
            () => service.ReserveSession(fixture.Session)).Message);
    }

    [Fact]
    public async Task AppLaunchAttachmentDisposalLeavesMonitorAliveAndReleasesReservation()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(new Gateway());
        await using (var result = await service.LaunchAppAndWaitForReservedSessionAsync(
            fixture.Session.AppId, fixture.Request, null, null, null, CancellationToken.None))
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(fixture.Session.SessionId, result.Session!.SessionId);
            Assert.Null(result.DeviceSession);
            Assert.NotNull(result.SessionClaim);
            Assert.Throws<InvalidOperationException>(() => service.ReserveSession(fixture.Session));
        }
        Assert.True(fixture.Runtime.IsSessionLive(fixture.Session.SessionId));
        using var next = service.ReserveSession(fixture.Session);
        Assert.Equal(0, fixture.Launcher.Stops);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task WorkspaceTestReusesCaptureAndReleasesInputOnPreparationFailureOrCancellation(bool explicitSession, bool cancel)
    {
        using var fixture = new Fixture();
        var gateway = new Gateway { Cancel = cancel };
        var service = fixture.Service(gateway);
        gateway.BeforePrepare = () => Assert.Throws<InvalidOperationException>(() => service.ReserveSession(fixture.Session));
        var request = new WorkspaceTestRunRequest(fixture.Workspace, "notes",
            SessionId: explicitSession ? fixture.Session.SessionId : null,
            Target: fixture.Request, EnableWorkspaceTools: false);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(request));
        else
        {
            var result = await service.RunAsync(request);
            Assert.Equal("test preparation reached", result.Message);
            Assert.Equal(fixture.Session.SessionId, result.SessionId);
        }
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(explicitSession ? 0 : 1, fixture.Launcher.Launches);
        Assert.Equal(0, fixture.Launcher.Stops);
        Assert.True(fixture.Runtime.IsSessionLive(fixture.Session.SessionId));
        using var next = service.ReserveSession(fixture.Session);
    }

    [Fact]
    public async Task BusyMonitorAttachmentDoesNotTransferAppCleanupOwnership()
    {
        using var fixture = new Fixture();
        var service = fixture.Service(new Gateway());
        using var busy = service.ReserveSession(fixture.Session);
        var cleanupTransferred = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchAppAndWaitForReservedSessionAsync(
            fixture.Session.AppId, fixture.Request, null, null, null, CancellationToken.None,
            onTargetLaunched: _ => cleanupTransferred = true));
        Assert.False(cleanupTransferred);
        Assert.True(fixture.Runtime.IsSessionLive(fixture.Session.SessionId));
    }

    [Fact]
    public async Task ExplicitSessionInfersDeviceModeAndRejectsWrongAppAndPlatform()
    {
        using var fixture = new Fixture();
        var gateway = new Gateway();
        var service = fixture.Service(gateway);
        var request = new WorkspaceTestRunRequest(fixture.Workspace, "notes", fixture.Session.SessionId,
            SessionWaitTimeout: TimeSpan.Zero, EnableWorkspaceTools: false);
        Assert.Equal("test preparation reached", (await service.RunAsync(request)).Message);
        var wrongPlatform = await service.RunAsync(request with { Target = new WorkspaceTestTargetRequest(Platform: "android") });
        Assert.Contains("platform does not match", wrongPlatform.Message);
        var wrongAppPath = Path.Combine(fixture.Workspace, "ansight", "tests", "notes.json");
        File.WriteAllText(wrongAppPath, File.ReadAllText(wrongAppPath).Replace("test.notes", "test.other"));
        Assert.False((await service.RunAsync(request)).IsSuccess);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(0, fixture.Launcher.Launches);
    }

    [Fact]
    public async Task CompletedCaptureCannotBeReusedForWorkspaceExecution()
    {
        using var fixture = new Fixture();
        var gateway = new Gateway();
        var service = fixture.Service(gateway);
        fixture.State.EndDeviceSession(fixture.Session.SessionId);
        var result = await service.RunAsync(new WorkspaceTestRunRequest(fixture.Workspace, "notes", fixture.Session.SessionId,
            SessionWaitTimeout: TimeSpan.Zero, EnableWorkspaceTools: false));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(0, fixture.Launcher.Launches);
    }

    [Fact]
    public void BatchKeepsDeviceModeWhenPinningAnAutomaticallySelectedTarget()
    {
        var target = new WorkspaceTestTarget("ios", "sim", "Phone", "test.notes", false, false, false)
        { ExecutionMode = "device", DeviceKind = DeviceKinds.Simulator };
        Assert.Equal("device", WorkspaceTestService.PinBatchTarget(null, target)!.ExecutionMode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        public RuntimeCoordinator Runtime { get; }
        public IRuntimeState State { get; }
        public AppSessionSnapshot Session { get; }
        public ReusingLauncher Launcher { get; }
        public string Workspace => environment.RootPath;
        public WorkspaceTestTargetRequest Request { get; } = new(Platform: "ios", DeviceIdentifier: "monitor-sim") { ExecutionMode = "device" };

        public Fixture()
        {
            var composition = new MefHostComposition(environment.ApplicationPaths,
                new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
            State = composition.Get<IRuntimeState>();
            Runtime = composition.Get<RuntimeCoordinator>();
            var target = new WorkspaceTestTarget("ios", "monitor-sim", "Phone", "test.notes", false, false, false)
            { ExecutionMode = "device", DeviceKind = DeviceKinds.Simulator };
            var id = State.CreateDeviceSession(target);
            State.TryGetSessionSnapshot(id, out var session);
            Session = session!;
            Launcher = new ReusingLauncher(target, Session);
            var directory = Path.Combine(Workspace, "ansight", "tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "notes.json"), """
                { "schemaVersion": 1, "name": "Notes", "appId": "test.notes",
                  "prompt": "Inspect the notes list.", "validation": "The notes list is visible." }
                """);
        }

        public WorkspaceTestService Service(Gateway gateway) => new(Runtime, Runtime.SimulatorAgent, Launcher, gateway);
        public void Dispose() { Runtime.Dispose(); environment.Dispose(); }
    }

    private sealed class ReusingLauncher(WorkspaceTestTarget target, AppSessionSnapshot session) : IWorkspaceTestTargetLauncher
    {
        public int Launches { get; private set; }
        public int Stops { get; private set; }
        public Task<WorkspaceTestTargetLaunchResult> LaunchAsync(string appId, WorkspaceTestTargetRequest? request,
            IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken)
        {
            Launches++;
            return Task.FromResult(WorkspaceTestTargetLaunchResult.Success(target) with { ExistingSession = session });
        }
        public Task<DeviceOperationResult> StopAsync(WorkspaceTestTarget target, CancellationToken cancellationToken)
        {
            Stops++;
            return Task.FromResult(DeviceOperationResult.Success("terminate-app", target.Platform, target.DeviceIdentifier, "Stopped."));
        }
    }

    private sealed class Gateway : IWorkspaceTestRunGateway
    {
        public int Calls { get; private set; }
        public bool Cancel { get; init; }
        public Action? BeforePrepare { get; set; }
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            BeforePrepare?.Invoke();
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(WorkspaceTestRunPreparation.Failure("test preparation reached"));
        }
        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Preparation failed; there should be no metering completion.");
    }
}
