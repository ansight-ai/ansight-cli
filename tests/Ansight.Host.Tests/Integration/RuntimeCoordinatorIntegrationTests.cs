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
    private static readonly JsonSerializerOptions protocolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task WriteArchiveTextEntryAsync(ZipArchive archive, string entryPath, string contents)
    {
        var entry = archive.CreateEntry(entryPath);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);
        await writer.WriteLineAsync(contents);
    }

    private static void WriteEncryptedArchive(
        string archivePath,
        string password,
        IReadOnlyDictionary<string, string> entries)
    {
        using var output = File.Create(archivePath);
        using var archive = new SharpZipOutputStream(output)
        {
            IsStreamOwner = false,
            Password = password
        };
        archive.SetLevel(1);

        foreach (var entryValue in entries)
        {
            var entry = new SharpZipEntry(entryValue.Key)
            {
                AESKeySize = 256
            };
            archive.PutNextEntry(entry);
            var bytes = Encoding.UTF8.GetBytes(entryValue.Value + Environment.NewLine);
            archive.Write(bytes);
            archive.CloseEntry();
        }

        archive.Finish();
    }

    private static async Task<UdpConnectResponse> SendConnectRequestAsync(
        SeededPairingConfig pairingConfig,
        string? tokenOverride = null,
        string? appIdOverride = null,
        string? processSessionId = null,
        IPAddress? hostAddress = null)
    {
        hostAddress ??= IPAddress.Loopback;
        using var client = new UdpClient(hostAddress.AddressFamily);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new EnrollmentConnectRequest
        {
            RequestId = CryptoUtil.CreateBase64UrlRandom(16),
            InviteId = pairingConfig.ConfigId,
            AccessToken = tokenOverride ?? pairingConfig.Token,
            AppId = appIdOverride ?? pairingConfig.AppId,
            DeviceId = $"integration-device-{pairingConfig.ConfigId}",
            DeviceName = "Integration Test Client",
            ProcessSessionId = processSessionId
        }, protocolJson);

        var remoteEndPoint = new IPEndPoint(hostAddress, ProtocolDefaults.DiscoveryPort);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await client.SendAsync(payload, payload.Length, remoteEndPoint);

            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            attemptTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));

            try
            {
                var receiveResult = await client.ReceiveAsync(attemptTimeout.Token);
                using var document = JsonDocument.Parse(receiveResult.Buffer);
                var root = document.RootElement;
                return new UdpConnectResponse(
                    root.GetProperty("accepted").GetBoolean(),
                    root.GetProperty("reason").GetString() ?? string.Empty,
                    root.TryGetProperty("reasonMessage", out var reasonMessageElement) ? reasonMessageElement.GetString() : null,
                    root.TryGetProperty("hostId", out var hostIdElement) ? hostIdElement.GetString() : null,
                    root.TryGetProperty("hostName", out var hostNameElement) ? hostNameElement.GetString() : null,
                    root.TryGetProperty("webSocketPort", out var portElement) && portElement.ValueKind != JsonValueKind.Null
                        ? portElement.GetInt32()
                        : null,
                    root.TryGetProperty("webSocketPath", out var pathElement) ? pathElement.GetString() : null,
                    root.TryGetProperty("webSocketToken", out var tokenElement) ? tokenElement.GetString() : null);
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, JsonObject payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonObject> ReceiveJsonAsync(ClientWebSocket socket)
    {
        var buffer = new byte[2048];
        using var stream = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Expected a JSON message but the WebSocket was closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))?.AsObject()
               ?? throw new InvalidOperationException("Expected a JSON object payload.");
    }

    private static async Task DrainSocketAsync(ClientWebSocket socket)
    {
        var buffer = new byte[2048];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
                }

                return;
            }
        }
    }

    private static byte[] CreateJpegPayload(
        DateTimeOffset capturedAtUtc,
        int width,
        int height,
        int quality,
        byte[] imageBytes)
    {
        var payload = new byte[28 + imageBytes.Length];
        payload[0] = (byte)'A';
        payload[1] = (byte)'S';
        payload[2] = (byte)'J';
        payload[3] = (byte)'P';
        payload[4] = 1;
        payload[5] = 1;
        payload[6] = (byte)quality;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), capturedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), imageBytes.Length);
        imageBytes.CopyTo(payload.AsSpan(28));
        return payload;
    }

    private static SeededPairingConfig SeedPortablePairingConfig(
        TestEnvironment environment,
        string appId,
        string appName,
        string hostId,
        string hostName)
    {
        var composition = new MefHostComposition(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var pairingCache = composition.Get<IPairingConfigCache>();
        var configId = Guid.NewGuid().ToString("N");
        var token = CryptoUtil.CreateBase64UrlRandom(32);
        var now = DateTimeOffset.UtcNow;

        pairingCache.Add(
            new PairingConfig
            {
                Schema = PairingConfig.SchemaName,
                ConfigId = configId,
                AppId = appId,
                AppName = appName,
                IssuedAt = now,
                ExpiresAt = now.AddMinutes(30),
                MinProtocolVersion = 2,
                AllowedTransports = [PairingTransportNames.Ws],
                Host = new PairingHost
                {
                    HostId = hostId,
                    HostName = hostName,
                    DiscoveryPort = ProtocolDefaults.DiscoveryPort
                },
                Enrollment = new PairingEnrollment
                {
                    Secret = token,
                    ExpiresAt = now.AddMinutes(30),
                    GrantExpiresAt = now.AddDays(14),
                    MaxUses = 1,
                    MaxToolPolicy = "read"
                }
            });

        return new SeededPairingConfig(configId, token, appId, appName);
    }

    private sealed record UdpConnectResponse(
        bool Accepted,
        string Reason,
        string? ReasonMessage,
        string? HostId,
        string? HostName,
        int? WebSocketPort,
        string? WebSocketPath,
        string? WebSocketToken);
}
