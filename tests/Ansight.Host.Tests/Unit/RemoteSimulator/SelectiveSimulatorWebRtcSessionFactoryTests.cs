using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class SelectiveSimulatorWebRtcSessionFactoryTests
{
    [Fact]
    public void Create_RoutesMacOsTargetToMacFactory()
    {
        var runtimeSource = new StubRuntimeSource(
            new RemoteRuntimeDevice(
                "macos-sdk:session-1",
                "Sample App",
                "Connected",
                true,
                "macOS 15",
                "macos"));
        var macFactory = new StubWebRtcSessionFactory();
        var factory = new SelectiveSimulatorWebRtcSessionFactory(
            runtimeSource,
            simulatorFactory: null,
            androidFactory: null,
            macFactory);

        var session = factory.Create("macos-sdk:session-1", 30);

        Assert.Equal("macos-sdk:session-1", session.DeviceUdid);
        Assert.Equal("macos-sdk:session-1", macFactory.LastCreatedDeviceIdentifier);
    }

    [Fact]
    public void SupportsDevice_DoesNotRouteUnknownTargetToMacFactory()
    {
        var runtimeSource = new StubRuntimeSource(
            new RemoteRuntimeDevice(
                "macos-sdk:session-1",
                "Sample App",
                "Connected",
                true,
                "macOS 15",
                "macos"));
        var factory = new SelectiveSimulatorWebRtcSessionFactory(
            runtimeSource,
            simulatorFactory: null,
            androidFactory: null,
            new StubWebRtcSessionFactory());

        Assert.False(factory.SupportsDevice("macos-sdk:unknown"));
    }

    private sealed class StubRuntimeSource : IRemoteRuntimeSource
    {
        public StubRuntimeSource(params RemoteRuntimeDevice[] devices)
        {
            Current = new RemoteRuntimeSnapshot(DateTimeOffset.UtcNow, devices, null);
        }

        public RemoteRuntimeSnapshot Current { get; }
    }

    private sealed class StubWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
    {
        public string? LastCreatedDeviceIdentifier { get; private set; }

        public string BackendName => "stub";

        public string Status => "ready";

        public bool SupportsDevice(string deviceUdid) => true;

        public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
        {
            LastCreatedDeviceIdentifier = deviceUdid;
            return new StubWebRtcSession(deviceUdid);
        }
    }

    private sealed class StubWebRtcSession : ISimulatorWebRtcSession
    {
        public StubWebRtcSession(string deviceUdid)
        {
            DeviceUdid = deviceUdid;
        }

        public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived
        {
            add { }
            remove { }
        }

        public string DeviceUdid { get; }

        public Task<WebRtcSessionDescription> CreateAnswerAsync(
            WebRtcSessionDescription offer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription("answer", offer.Sdp));

        public bool TrySendMessage(string message) => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
