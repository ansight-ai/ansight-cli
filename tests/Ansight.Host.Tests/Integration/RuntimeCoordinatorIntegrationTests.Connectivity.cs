using System.Buffers.Binary;
using System.IO.Compression;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task EnrollmentOverIpv6_CompletesWebSocketUpgrade()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.ipv6-testapp", "IPv6 Test App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(
            pairingConfig,
            hostAddress: IPAddress.IPv6Loopback);
        Assert.True(connectResponse.Accepted, connectResponse.ReasonMessage);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.NotNull(connectResponse.WebSocketPath);
        Assert.NotNull(connectResponse.WebSocketToken);

        using var socket = new ClientWebSocket();
        var socketUri = new UriBuilder(
            "ws",
            IPAddress.IPv6Loopback.ToString(),
            connectResponse.WebSocketPort.Value,
            connectResponse.WebSocketPath)
        {
            Query = $"token={connectResponse.WebSocketToken}"
        }.Uri;
        await socket.ConnectAsync(socketUri, CancellationToken.None);
        await TestWait.UntilAsync(
            () => runtime.AppTools.GetConnectedSessionIds().Count == 1,
            because: "Expected the IPv6 WebSocket session to become connected.");
        socket.Abort();

        await runtime.StopAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task ForceDisconnectSession_WhenSessionIsConnected_RemovesConnection()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.force-disconnect", "Force Disconnect App");
        using var runtime = environment.CreateRuntime();
        var captureEvents = new ConcurrentQueue<RuntimeSessionCaptureEvent>();
        runtime.SessionCaptureEventOccurred += (_, runtimeEvent) => captureEvents.Enqueue(runtimeEvent);

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                var connectedSessions = runtime.AppTools.GetConnectedSessionIds();
                if (connectedSessions.Count != 1)
                {
                    return false;
                }

                sessionId = connectedSessions[0];
                return true;
            },
            because: "The paired app should appear as a connected session.");

        var result = runtime.AppTools.ForceDisconnect(sessionId!);

        Assert.True(result.IsSuccess, result.Message);
        await TestWait.UntilAsync(
            () =>
            {
                if (runtime.AppTools.GetConnectedSessionIds().Count != 0)
                {
                    return false;
                }

                return runtime.Sessions.TryGetSnapshot(sessionId!, out var snapshot)
                       && snapshot is not null
                       && string.Equals(snapshot.Status, "WebSocket Closed", StringComparison.Ordinal)
                       && captureEvents.Any(runtimeEvent =>
                           runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Stopped
                           && runtimeEvent.SessionId == sessionId
                           && string.Equals(runtimeEvent.Message, "Force disconnected from the host.", StringComparison.Ordinal));
            },
            timeout: TimeSpan.FromSeconds(15),
            because: "Force disconnect should close the live bridge and persist the disconnect state.");

        Assert.False(runtime.AppTools.IsConnected(sessionId!));
        Assert.True(runtime.Sessions.TryGetSnapshot(sessionId!, out var finalSnapshot));
        Assert.NotNull(finalSnapshot);
        Assert.Equal("WebSocket Closed", finalSnapshot!.Status);
        Assert.Contains(
            captureEvents,
            runtimeEvent => runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Stopped
                            && runtimeEvent.SessionId == sessionId
                            && string.Equals(runtimeEvent.Message, "Force disconnected from the host.", StringComparison.Ordinal));
    }

    [Fact(Timeout = 60000)]
    public async Task InvalidPairingRequest_RejectedAndEmitsPairingRejectedEvent()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.testapp", "Example Test App");
        using var runtime = environment.CreateRuntime();
        var pairingEvents = new ConcurrentQueue<RuntimePairingEvent>();
        runtime.PairingEventOccurred += (_, runtimeEvent) => pairingEvents.Enqueue(runtimeEvent);

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig, tokenOverride: "wrong-token");

        Assert.False(connectResponse.Accepted);
        Assert.Equal("AccessTokenInvalid", connectResponse.Reason);
        RuntimePairingEvent? rejectedEvent = null;
        await TestWait.UntilAsync(
            () =>
            {
                rejectedEvent = pairingEvents.FirstOrDefault(runtimeEvent => runtimeEvent.Kind == RuntimePairingEventKind.PairingRejected);
                return rejectedEvent is not null;
            },
            because: "Rejected pairing attempts should emit a rejection event.");

        Assert.NotNull(rejectedEvent);
        Assert.Empty(runtime.Sessions.GetSummaries());
        if (!string.IsNullOrWhiteSpace(rejectedEvent!.SessionId))
        {
            Assert.False(runtime.Sessions.TryGetSnapshot(rejectedEvent.SessionId, out _));
        }
    }

    [Fact(Timeout = 60000)]
    public async Task ConnectRequest_WithoutAuthenticatedAccount_IsAcceptedForLocalUse()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.testapp", "Example Test App");
        using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);

        Assert.True(connectResponse.Accepted, connectResponse.ReasonMessage);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));
    }

    [Fact(Timeout = 60000)]
    public async Task WebSocketHandshake_WhenAccountAuthenticationIsCleared_RemainsAvailableForLocalUse()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.testapp", "Example Test App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");

        await socket.ConnectAsync(socketUri, CancellationToken.None);

        await TestWait.UntilAsync(
            () => runtime.AppTools.GetConnectedSessionIds().Count == 1,
            because: "Account state must not block the free local WebSocket bridge.");
        socket.Abort();
    }

    [Fact(Timeout = 60000)]
    public async Task ConnectResponse_UsesCurrentHostIdentity()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = SeedPortablePairingConfig(
            environment,
            "com.example.portable",
            "Portable App",
            "portable-host-id",
            "Portable Host");
        var composition = new MefHostComposition(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var currentHost = composition.Get<IIdentityStore>().Current;
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);

        Assert.True(connectResponse.Accepted);
        Assert.Equal(currentHost.HostId, connectResponse.HostId);
        Assert.Equal(currentHost.HostName, connectResponse.HostName);
    }
}
