using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerCompanionConnectionTests
{
    private const string SessionToken = "companion-connection-test-token";

    [Fact]
    public async Task CompanionPresence_TracksConnectedDeviceUntilSessionCloses()
    {
        var sessionFactory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(sessionFactory);
        using var connectionChanged = new SemaphoreSlim(0);
        server.CompanionConnectionsChanged += (_, _) => connectionChanged.Release();
        await server.StartAsync();
        using var client = new HttpClient();

        var offerBody = JsonSerializer.Serialize(
            new
            {
                deviceUdid = "runtime-device-1",
                framesPerSecond = 30,
                type = "offer",
                sdp = "v=0\r\n",
                grantedScopes = new[] { "view" }
            });
        using var offerResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(offerResponse.IsSuccessStatusCode, await offerResponse.Content.ReadAsStringAsync());
        var offerAnswer = await offerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = offerAnswer.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Empty(server.GetConnectedCompanionConnections());

        var session = Assert.IsType<FakeWebRtcSession>(sessionFactory.LastSession);
        session.Receive("""
            {
              "kind": "companion_presence",
              "deviceId": "companion-device-1",
              "deviceName": "Matthew's iPhone",
              "model": "iPhone",
              "platform": "iOS",
              "operatingSystemVersion": "26.5.2",
              "appVersion": "0.17.1 (2026080500)"
            }
            """);

        Assert.True(await connectionChanged.WaitAsync(TimeSpan.FromSeconds(2)));
        var connection = Assert.Single(server.GetConnectedCompanionConnections());
        Assert.Equal(sessionId, connection.SessionId);
        Assert.Equal("companion-device-1", connection.Device.Identifier);
        Assert.Equal("Matthew's iPhone", connection.Device.Name);
        Assert.Equal("iOS", connection.Device.Platform);
        Assert.Equal("26.5.2", connection.Device.OperatingSystemVersion);
        Assert.Equal("runtime-device-1", connection.TargetDeviceUdid);

        using var closeResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/close", $"session={Uri.EscapeDataString(sessionId!)}"),
            content: null);
        Assert.True(closeResponse.IsSuccessStatusCode, await closeResponse.Content.ReadAsStringAsync());
        Assert.True(await connectionChanged.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(server.GetConnectedCompanionConnections());
    }

    [Fact]
    public async Task DisconnectCompanionAsync_ClosesTrackedSession()
    {
        var sessionFactory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(sessionFactory);
        await server.StartAsync();
        using var client = new HttpClient();
        var offerBody = JsonSerializer.Serialize(
            new
            {
                deviceUdid = "runtime-device-1",
                framesPerSecond = 30,
                type = "offer",
                sdp = "v=0\r\n",
                grantedScopes = new[] { "view" }
            });
        using var offerResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/offer"),
            new StringContent(offerBody, Encoding.UTF8, "application/json"));
        Assert.True(offerResponse.IsSuccessStatusCode, await offerResponse.Content.ReadAsStringAsync());
        var offerAnswer = await offerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = offerAnswer.GetProperty("sessionId").GetString()!;
        var session = Assert.IsType<FakeWebRtcSession>(sessionFactory.LastSession);
        session.Receive("""
            {
              "kind": "companion_presence",
              "deviceId": "companion-device-1",
              "deviceName": "Matthew's iPhone"
            }
            """);

        Assert.True(await server.DisconnectCompanionAsync(sessionId));
        Assert.Empty(server.GetConnectedCompanionConnections());
        Assert.False(await server.DisconnectCompanionAsync(sessionId));
    }

    private static RemoteControlServer CreateServer(ISimulatorWebRtcSessionFactory sessionFactory)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            webRtcSessionFactory: sessionFactory);

    private static string Endpoint(RemoteControlServer server, string path, string? query = null)
    {
        var suffix = string.IsNullOrWhiteSpace(query) ? string.Empty : $"&{query}";
        return $"http://127.0.0.1:{server.Port}{path}?token={SessionToken}{suffix}";
    }

    private sealed class FakeRuntimeSource : IRemoteRuntimeSource
    {
        public RemoteRuntimeSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            [
                new RemoteRuntimeDevice(
                    "runtime-device-1",
                    "iPhone 17 Pro Max",
                    "Booted",
                    true,
                    "iOS 26.5",
                    "ios")
            ],
            null);
    }

    private sealed class FakeWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
    {
        public string BackendName => "fake";

        public string Status => "Ready";

        public ISimulatorWebRtcSession? LastSession { get; private set; }

        public bool SupportsDevice(string deviceUdid) => true;

        public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
        {
            LastSession = new FakeWebRtcSession(deviceUdid);
            return LastSession;
        }
    }

    private sealed class FakeWebRtcSession : ISimulatorWebRtcSession
    {
        public FakeWebRtcSession(string deviceUdid)
        {
            DeviceUdid = deviceUdid;
        }

        public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived;

        public string DeviceUdid { get; }

        public Task<WebRtcSessionDescription> CreateAnswerAsync(
            WebRtcSessionDescription offer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription("answer", offer.Sdp));

        public bool TrySendMessage(string message) => true;

        public void Receive(string message)
            => InputReceived?.Invoke(this, new WebRtcInputMessageEventArgs(message));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
