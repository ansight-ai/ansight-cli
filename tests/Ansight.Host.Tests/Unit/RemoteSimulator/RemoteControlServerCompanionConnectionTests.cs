using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Devices;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Server;
using Ansight.RemoteSimulator.Core.Streaming;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class RemoteControlServerCompanionConnectionTests
{
    private const string SessionToken = "companion-connection-test-token";

    [Fact]
    public async Task ViewOnlyGuests_CannotCloseOrImpersonateAnotherCompanion()
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var firstLease = Guid.NewGuid().ToString("D");
        var secondLease = Guid.NewGuid().ToString("D");
        var firstId = await OpenCompanionAsync(server, client, factory, firstLease, "same-reported-phone");
        var first = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        var secondId = await OpenCompanionAsync(server, client, factory, secondLease, "same-reported-phone");
        var second = Assert.IsType<FakeWebRtcSession>(factory.LastSession);

        using var wrongClose = await client.PostAsync(
            Endpoint(server, "/api/webrtc/close", $"session={firstId}&lease={secondLease}"), null);

        Assert.Equal(HttpStatusCode.Forbidden, wrongClose.StatusCode);
        Assert.False(first.IsDisposed);
        Assert.True(await server.DisconnectCompanionAsync(secondId));
        Assert.True(second.IsDisposed);
        Assert.False(first.IsDisposed);

        using var ownClose = await client.PostAsync(
            Endpoint(server, "/api/webrtc/close", $"session={firstId}&lease={firstLease}"), null);

        Assert.True(ownClose.IsSuccessStatusCode);
        Assert.True(first.IsDisposed);
        Assert.False(server.IsCompanionLeaseDisconnected(firstLease));
    }

    [Fact]
    public async Task CompanionPresence_TracksConnectedDeviceUntilSessionCloses()
    {
        var sessionFactory = new FakeWebRtcSessionFactory();
        using var connectionChanged = new SemaphoreSlim(0);
        await using var server = CreateServer(sessionFactory);
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

        using var activityResponse = await client.PostAsync(
            Endpoint(server, "/api/device/testing"),
            new StringContent(JsonSerializer.Serialize(new RemoteDeviceTestingRequest("runtime-device-1", "device-activity")), Encoding.UTF8, "application/json"));
        activityResponse.EnsureSuccessStatusCode();
        var activity = await activityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reported = Assert.Single(activity.GetProperty("details").GetProperty("companionConnections").EnumerateArray());
        Assert.Equal("Matthew's iPhone", reported.GetProperty("device").GetProperty("name").GetString());
        Assert.Equal("runtime-device-1", reported.GetProperty("targetDeviceUdid").GetString());

        using var closeResponse = await client.PostAsync(
            Endpoint(server, "/api/webrtc/close", $"session={Uri.EscapeDataString(sessionId!)}"),
            content: null);
        Assert.True(closeResponse.IsSuccessStatusCode, await closeResponse.Content.ReadAsStringAsync());
        Assert.True(await connectionChanged.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(server.GetConnectedCompanionConnections());

        using var closedActivityResponse = await client.PostAsync(
            Endpoint(server, "/api/device/testing"),
            new StringContent(JsonSerializer.Serialize(new RemoteDeviceTestingRequest("runtime-device-1", "device-activity")), Encoding.UTF8, "application/json"));
        var closedActivity = await closedActivityResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(closedActivity.GetProperty("details").GetProperty("companionConnections").EnumerateArray());
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

    [Fact]
    public async Task MachineConnection_IsVisibleAndCanBeDisconnectedWithoutVideo()
    {
        await using var server = CreateServer(new FakeWebRtcSessionFactory());
        await server.StartAsync();
        var leaseId = Guid.NewGuid().ToString("D");
        var presence = JsonSerializer.SerializeToElement(new
        {
            deviceId = "phone-1",
            deviceName = "Test phone",
            platform = "iOS"
        });
        server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(90), presence);
        var connection = Assert.Single(server.GetConnectedCompanionConnections());
        Assert.Equal("Test phone", connection.Device.Name);
        Assert.Equal("companion:" + leaseId, connection.SessionId);

        server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(90), null);
        Assert.Equal(connection, Assert.Single(server.GetConnectedCompanionConnections()));
        Assert.True(await server.DisconnectCompanionAsync(connection.SessionId));
        Assert.Empty(server.GetConnectedCompanionConnections());
        Assert.Throws<UnauthorizedAccessException>(() =>
            server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(90), presence));
    }

    [Fact]
    public async Task MachineConnection_ExpiresWhenItsClientStopsRenewing()
    {
        await using var server = CreateServer(new FakeWebRtcSessionFactory());
        await server.StartAsync();
        var leaseId = Guid.NewGuid().ToString("D");
        server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(90), null);
        Assert.Single(server.GetConnectedCompanionConnections());
        server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(-1), null);
        await server.RemoveExpiredWebRtcSessionsAsync();
        Assert.Empty(server.GetConnectedCompanionConnections());
    }

    [Fact]
    public async Task DisconnectMachineConnection_ClosesItsVideoAndPreservesOtherClients()
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var leaseId = Guid.NewGuid().ToString("D");
        server.TrackCompanionAccess(leaseId, DateTimeOffset.UtcNow.AddSeconds(90), null);
        await OpenCompanionAsync(server, client, factory, leaseId, "phone-1");
        var video = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        var connection = Assert.Single(server.GetConnectedCompanionConnections());
        Assert.Equal("phone-1", connection.Device.Name);
        Assert.Equal("runtime-device-1", connection.TargetDeviceUdid);
        await OpenCompanionAsync(server, client, factory, Guid.NewGuid().ToString("D"), "phone-2");

        Assert.True(await server.DisconnectCompanionAsync(connection.SessionId));
        Assert.True(video.IsDisposed);
        Assert.Equal("phone-2", Assert.Single(server.GetConnectedCompanionConnections()).Device.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_ClosesAllDeviceSessionsAndRejectsAutomaticReconnect(bool disconnectAll)
    {
        var factory = new FakeWebRtcSessionFactory();
        await using var server = CreateServer(factory);
        await server.StartAsync();
        using var client = new HttpClient();
        var leaseId = Guid.NewGuid().ToString("D");
        var firstId = await OpenCompanionAsync(server, client, factory, leaseId, "phone-1");
        var first = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        await OpenCompanionAsync(server, client, factory, leaseId, "phone-1");
        var second = Assert.IsType<FakeWebRtcSession>(factory.LastSession);
        var otherLease = Guid.NewGuid().ToString("D");
        await OpenCompanionAsync(server, client, factory, otherLease, "phone-2");
        var other = Assert.IsType<FakeWebRtcSession>(factory.LastSession);

        if (disconnectAll)
        {
            Assert.Equal(3, await server.DisconnectAllCompanionsAsync());
        }
        else
        {
            Assert.True(await server.DisconnectCompanionAsync(firstId));
        }

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(disconnectAll, other.IsDisposed);
        Assert.True(server.IsCompanionLeaseDisconnected(leaseId));
        Assert.Equal(disconnectAll, server.IsCompanionLeaseDisconnected(otherLease));

        using var retry = await client.PostAsync(Endpoint(server, "/api/webrtc/offer"), new StringContent(JsonSerializer.Serialize(new
        {
            deviceUdid = "runtime-device-1",
            framesPerSecond = 30,
            type = "offer",
            sdp = "v=0\r\n",
            grantedScopes = new[] { "view" },
            authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);

        await OpenCompanionAsync(server, client, factory, Guid.NewGuid().ToString("D"), "phone-1");
    }

    private static async Task<string> OpenCompanionAsync(RemoteControlServer server, HttpClient client,
        FakeWebRtcSessionFactory factory, string leaseId, string deviceId)
    {
        using var response = await client.PostAsync(Endpoint(server, "/api/webrtc/offer"), new StringContent(JsonSerializer.Serialize(new
        {
            deviceUdid = "runtime-device-1",
            framesPerSecond = 30,
            type = "offer",
            sdp = "v=0\r\n",
            grantedScopes = new[] { "view" },
            authorizationLeaseId = leaseId,
            authorizationExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(90)
        }), Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = answer.GetProperty("sessionId").GetString()!;
        using var changed = new SemaphoreSlim(0);
        EventHandler onChanged = (_, _) => changed.Release();
        server.CompanionConnectionsChanged += onChanged;

        try
        {
            Assert.IsType<FakeWebRtcSession>(factory.LastSession).Receive(JsonSerializer.Serialize(new
            {
                kind = "companion_presence", deviceId, deviceName = deviceId
            }));
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            server.CompanionConnectionsChanged -= onChanged;
        }

        return id;
    }

    private static RemoteControlServer CreateServer(ISimulatorWebRtcSessionFactory sessionFactory)
        => new(
            new FakeRuntimeSource(),
            new FakeRemoteSimulatorFrameSource(),
            new FakeRemoteSimulatorInputSink(),
            requestedPort: 0,
            sessionToken: SessionToken,
            webRtcSessionFactory: sessionFactory,
            deviceTestingSource: new FakeDeviceTestingSource());

    private sealed class FakeDeviceTestingSource : IRemoteDeviceTestingSource
    {
        public Task<RemoteDeviceTestingResult> ExecuteAsync(
            RemoteDeviceTestingRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteDeviceTestingResult(true, "Activity loaded."));
        }
    }

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

        public bool IsDisposed { get; private set; }

        public Task<WebRtcSessionDescription> CreateAnswerAsync(
            WebRtcSessionDescription offer,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WebRtcSessionDescription("answer", offer.Sdp));

        public bool TrySendMessage(string message) => true;

        public void Receive(string message)
            => InputReceived?.Invoke(this, new WebRtcInputMessageEventArgs(message));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;

            return ValueTask.CompletedTask;
        }
    }
}
