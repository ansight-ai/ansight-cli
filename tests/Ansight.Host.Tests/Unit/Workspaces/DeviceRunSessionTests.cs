using Ansight.Host.Runtime.Screenshots;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class DeviceRunSessionTests
{
    [Fact]
    public void PhysicalIosWatchSessionRetainsPhysicalDeviceIdentity()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "core-device-id", "iPhone",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        Assert.False(session!.DeviceProfile!.Device!.IsVirtual);
        Assert.False(session.DeviceProfile.Device.IsEmulator);
        Assert.Contains("core-device-id", session.DeviceProfileJson);
    }

    [Fact]
    public void PhysicalAndroidWatchSessionRetainsPhysicalDeviceIdentity()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("android", "R58N123", "Pixel",
            "test.app", false, false, false) { DeviceKind = DeviceKinds.Device, ExecutionMode = "device" });
        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        Assert.False(session!.DeviceProfile!.Device!.IsVirtual);
        Assert.False(session.DeviceProfile.Device.IsEmulator);
        Assert.Contains("R58N123", session.DeviceProfileJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalRevokesSessionAndReleasesDeviceEvenWhenCaptureStopFails(bool failStop)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var device = new DeviceDescriptor(Guid.NewGuid().ToString(), "Phone", "ios", "test-runtime", "booted", true, true, DeviceKinds.Simulator);
        var claim = DeviceExecutionClaim.Acquire(device);
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", device.Identifier, device.Name, "test.app", false, false, true)
            { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" });
        Assert.True(state.TryGetSessionSnapshot(id, out var session));
        var captures = new CaptureManager(state, failStop);
        var finalized = 0;
        var releases = 0;
        var drained = false;
        var instrumentsStopped = false;
        state.RuntimeEventOccurred += (_, value) =>
        {
            if (value is RuntimeSessionCaptureEvent { Kind: RuntimeSessionCaptureEventKind.Finalized })
            {
                Assert.True(drained);
                Assert.True(instrumentsStopped);
                Assert.Equal(1, captures.Stops);
                finalized++;
            }
        };
        var lease = new DeviceRunSession(session!, state, captures, claim,
            drainTriggers: (sessionId, _) =>
            {
                Assert.Equal(id, sessionId);
                Assert.False(state.IsDeviceSessionActive(id));
                Assert.Equal(0, captures.Stops);
                drained = true;
                return Task.CompletedTask;
            }, releaseResources: () =>
            {
                releases++;
                return Task.CompletedTask;
            }, stopAdditionalEvidence: () =>
            {
                Assert.True(state.IsDeviceSessionActive(id));
                instrumentsStopped = true;
                return Task.CompletedTask;
            });
        if (failStop) await Assert.ThrowsAsync<IOException>(async () => await lease.DisposeAsync());
        else await lease.DisposeAsync();
        Assert.False(state.IsDeviceSessionActive(id));
        using var replacement = DeviceExecutionClaim.Acquire(device);
        await lease.DisposeAsync();
        Assert.Equal(1, captures.Stops);
        Assert.Equal(1, finalized);
        Assert.Equal(1, releases);
    }

    private sealed class CaptureManager(IRuntimeState state, bool failStop) : IExternalSessionScreenshotCaptureManager
    {
        public event EventHandler<ExternalSessionScreenshotCaptureFailedEventArgs>? CaptureFailed { add { } remove { } }
        public int Stops { get; private set; }
        public Task<ExternalSessionScreenshotCapturePolicy> AttachAsync(string sessionId, DeviceAppProfile? profile,
            string? profileJson, ExternalSessionScreenshotCaptureRequest captureRequest, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IDisposable BeginTestRun(string sessionId) => throw new NotSupportedException();
        public void SetInterval(string sessionId, int intervalMilliseconds) => throw new NotSupportedException();
        public Task StopAsync(string sessionId, string reason)
        {
            Stops++;
            Assert.False(state.IsDeviceSessionActive(sessionId));
            return failStop ? Task.FromException(new IOException("Capture stop failed")) : Task.CompletedTask;
        }
    }
}
