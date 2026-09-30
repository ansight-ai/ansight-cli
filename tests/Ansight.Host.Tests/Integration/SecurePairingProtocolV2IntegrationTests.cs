using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class EnrollmentProtocolIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task CrashHandoff_AfterAppRestart_IsDurableAndIdempotentOverWebSocket()
    {
        using var environment = new TestEnvironment(webSocketSessionPortCount: 2);
        using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var previousProcess = Guid.NewGuid().ToString("D");
        var currentProcess = Guid.NewGuid().ToString("D");
        var deviceId = $"crash-test-{Guid.NewGuid():N}";
        var accessToken = CryptoUtil.CreateBase64UrlRandom(32);
        EnrollmentConnectRequest Request(string process) => new()
        {
            EnrollmentMode = EnrollmentConnectRequest.LocalMode,
            RequestId = CryptoUtil.CreateBase64UrlRandom(16),
            InviteId = string.Empty,
            AppId = "com.example.crash-handoff",
            DeviceId = deviceId,
            DeviceName = "Crash Handoff Test",
            AccessToken = accessToken,
            ProcessSessionId = process
        };
        var original = await SendEnrollmentRequestAsync(Request(previousProcess), IPAddress.Loopback);
        Assert.True(original.Accepted, original.ReasonMessage);
        var previousSession = Assert.Single(runtime.Sessions.GetSummaries()).SessionId;
        var response = await SendEnrollmentRequestAsync(Request(currentProcess), IPAddress.Loopback);
        Assert.True(response.Accepted, response.ReasonMessage);
        using var socket = new ClientWebSocket();
        var uri = new UriBuilder("ws", IPAddress.Loopback.ToString(), response.WebSocketPort!.Value, response.WebSocketPath)
        {
            Query = $"token={Uri.EscapeDataString(response.WebSocketToken!)}"
        }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(uri, timeout.Token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var requestId = $"crash-handoff-{attempt}";
            var request = new PairingControlEnvelope
            {
                Type = PairingControlEnvelope.RequestType,
                Id = requestId,
                Action = "crash.handoff",
                Payload = new JsonObject
                {
                    ["reportId"] = "recovered-crash",
                    ["targetProcessSessionId"] = previousProcess,
                    ["targetSessionId"] = previousSession,
                    ["deliveryProcessSessionId"] = currentProcess,
                    ["report"] = new JsonObject
                    {
                        ["schema"] = "ansight.crash.v1",
                        ["reportId"] = "recovered-crash",
                        ["previousProcessSessionId"] = previousProcess,
                        ["occurredAtUtc"] = "2026-09-04T02:00:00Z",
                        ["hostAcknowledged"] = false
                    }
                }
            };
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request, JsonUtil.Compact),
                WebSocketMessageType.Text, true, timeout.Token);
            while (true)
            {
                using var message = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    Assert.NotEqual(WebSocketMessageType.Close, received.MessageType);
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var reply = JsonNode.Parse(message.ToArray())!.AsObject();
                if (reply["replyTo"]?.GetValue<string>() != requestId) continue;
                Assert.True(reply["success"]!.GetValue<bool>(), reply["message"]?.GetValue<string>());
                break;
            }
            var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
            Assert.True(restartedStore.TryLoad(previousSession, out var retained));
            Assert.Single(retained!.ArtifactSnapshots);
        }
        socket.Abort();
        await runtime.StopAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task LocalEnrollment_RegistersLoopbackAppWithoutInvite()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var request = new EnrollmentConnectRequest
        {
            EnrollmentMode = EnrollmentConnectRequest.LocalMode,
            RequestId = CryptoUtil.CreateBase64UrlRandom(16),
            InviteId = string.Empty,
            AppId = "com.example.local-enrollment",
            DeviceId = $"test-device-{Guid.NewGuid():N}",
            DeviceName = "Local Integration Test",
            AccessToken = CryptoUtil.CreateBase64UrlRandom(32),
            ProcessSessionId = $"test-process-{Guid.NewGuid():N}"
        };

        var response = await SendEnrollmentRequestAsync(request, IPAddress.Loopback);

        Assert.True(response.Accepted, response.ReasonMessage);
        Assert.Equal("Ok", response.Reason);
        Assert.NotNull(response.WebSocketPort);

        await runtime.StopAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task LocalEnrollment_WithoutAuthenticatedAccount_IsAccepted()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var request = new EnrollmentConnectRequest
        {
            EnrollmentMode = EnrollmentConnectRequest.LocalMode,
            RequestId = CryptoUtil.CreateBase64UrlRandom(16),
            InviteId = string.Empty,
            AppId = "com.example.local-enrollment",
            DeviceId = $"test-device-{Guid.NewGuid():N}",
            DeviceName = "Local Integration Test",
            AccessToken = CryptoUtil.CreateBase64UrlRandom(32),
            ProcessSessionId = $"test-process-{Guid.NewGuid():N}"
        };

        var response = await SendEnrollmentRequestAsync(request, IPAddress.Loopback);

        Assert.True(response.Accepted, response.ReasonMessage);
        Assert.Equal("Ok", response.Reason);
        Assert.NotNull(response.WebSocketPort);
        Assert.False(string.IsNullOrWhiteSpace(response.WebSocketToken));

        await runtime.StopAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task LocalEnrollment_HandlesConcurrentAppStarts()
    {
        using var environment = new TestEnvironment(webSocketSessionPortCount: 12);
        using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();

        var responseTasks = Enumerable.Range(0, 12)
            .Select(index => SendEnrollmentRequestAsync(
                new EnrollmentConnectRequest
                {
                    EnrollmentMode = EnrollmentConnectRequest.LocalMode,
                    RequestId = CryptoUtil.CreateBase64UrlRandom(16),
                    InviteId = string.Empty,
                    AppId = $"com.example.concurrent-local-{index}",
                    DeviceId = $"test-device-{Guid.NewGuid():N}",
                    DeviceName = $"Concurrent Local Test {index}",
                    AccessToken = CryptoUtil.CreateBase64UrlRandom(32),
                    ProcessSessionId = $"test-process-{Guid.NewGuid():N}"
                },
                IPAddress.Loopback))
            .ToArray();

        var responses = await Task.WhenAll(responseTasks);

        Assert.All(responses, response =>
        {
            Assert.True(response.Accepted, response.ReasonMessage);
            Assert.Equal("Ok", response.Reason);
            Assert.NotNull(response.WebSocketPort);
        });

        await runtime.StopAsync();
    }

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrollmentFlow_RegistersInstallationAndIssuesClearTextWebSocket(bool useIpv6)
    {
        if (useIpv6 && !Socket.OSSupportsIPv6)
        {
            return;
        }

        var loopbackAddress = useIpv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var environment = new TestEnvironment();
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        var composition = new MefHostComposition(environment.ApplicationPaths, storage);
        var issueResult = composition.Get<IPairingConfigService>().Issue(
            "Enrollment Protocol Test",
            "com.example.enrollment-protocol-test",
            PairingConfigDuration.Default);
        var pairingCache = composition.Get<IPairingConfigCache>();
        var config = Assert.IsType<PairingConfig>(pairingCache.Find(issueResult.ConfigId)?.Config);
        var accessToken = Assert.IsType<string>(config.Enrollment?.Secret);
        var deviceId = $"test-device-{Guid.NewGuid():N}";

        try
        {
            using var runtime = environment.CreateRuntime();
            await runtime.StartAsync();

            var request = new EnrollmentConnectRequest
            {
                RequestId = CryptoUtil.CreateBase64UrlRandom(16),
                InviteId = config.ConfigId,
                AppId = config.AppId,
                DeviceId = deviceId,
                DeviceName = "Integration Test Device",
                AccessToken = accessToken,
                ProcessSessionId = $"test-process-{Guid.NewGuid():N}"
            };
            var response = await SendEnrollmentRequestAsync(request, loopbackAddress);

            Assert.True(response.Accepted, response.ReasonMessage);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal("Ok", response.Reason);
            Assert.Equal(config.Host.HostId, response.HostId);
            Assert.NotNull(response.WebSocketPort);
            Assert.False(string.IsNullOrWhiteSpace(response.WebSocketPath));
            Assert.False(string.IsNullOrWhiteSpace(response.WebSocketToken));
            Assert.True(pairingCache.HasActiveGrant(config.ConfigId, config.AppId));

            using var socket = new ClientWebSocket();
            var socketUri = new UriBuilder(
                Uri.UriSchemeWs,
                loopbackAddress.ToString(),
                response.WebSocketPort.Value,
                response.WebSocketPath)
            {
                Query = $"token={Uri.EscapeDataString(response.WebSocketToken)}"
            }.Uri;
            await socket.ConnectAsync(socketUri, CancellationToken.None);
            Assert.Equal(WebSocketState.Open, socket.State);

            socket.Abort();
            await runtime.StopAsync();
        }
        finally
        {
            if (File.Exists(issueResult.DesktopPath))
            {
                File.Delete(issueResult.DesktopPath);
            }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task GenericEnrollment_RegistersPreviouslyUnknownScanningApp()
    {
        using var environment = new TestEnvironment();
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        var composition = new MefHostComposition(environment.ApplicationPaths, storage);
        var issueResult = composition.Get<IPairingConfigService>().Issue(
            PairingConfig.AnyAppName,
            PairingConfig.AnyAppId,
            PairingConfigDuration.Default);
        var pairingCache = composition.Get<IPairingConfigCache>();
        var config = Assert.IsType<PairingConfig>(pairingCache.Find(issueResult.ConfigId)?.Config);
        var accessToken = Assert.IsType<string>(config.Enrollment?.Secret);
        const string scanningAppId = "com.example.previously-unknown";

        try
        {
            using var runtime = environment.CreateRuntime();
            await runtime.StartAsync();

            var response = await SendEnrollmentRequestAsync(
                new EnrollmentConnectRequest
                {
                    RequestId = CryptoUtil.CreateBase64UrlRandom(16),
                    InviteId = config.ConfigId,
                    AppId = scanningAppId,
                    DeviceId = $"test-device-{Guid.NewGuid():N}",
                    DeviceName = "Generic Enrollment Test",
                    AccessToken = accessToken,
                    ProcessSessionId = $"test-process-{Guid.NewGuid():N}"
                },
                IPAddress.Loopback);

            Assert.True(response.Accepted, response.ReasonMessage);
            Assert.True(pairingCache.HasActiveGrant(config.ConfigId, scanningAppId));
            Assert.True(composition.Get<IKnownAppStore>().TryGet(scanningAppId, out var knownApp));
            Assert.Equal(scanningAppId, knownApp?.Name);

            await runtime.StopAsync();
        }
        finally
        {
            if (File.Exists(issueResult.DesktopPath))
            {
                File.Delete(issueResult.DesktopPath);
            }
        }
    }

    private static async Task<ConnectResponse> SendEnrollmentRequestAsync(
        EnrollmentConnectRequest request,
        IPAddress hostAddress)
    {
        using var udp = new UdpClient(hostAddress.AddressFamily);
        udp.Connect(hostAddress, ProtocolDefaults.DiscoveryPort);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonUtil.Compact);
        await udp.SendAsync(payload, payload.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await udp.ReceiveAsync(timeout.Token);
        return Assert.IsType<ConnectResponse>(
            JsonSerializer.Deserialize<ConnectResponse>(response.Buffer, JsonUtil.Compact));
    }
}
