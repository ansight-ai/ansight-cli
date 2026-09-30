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
    public async Task PairingAndStreamingFlow_EmitsEventsAndPersistsSessionSnapshot()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.testapp", "Example Test App");
        using var runtime = environment.CreateRuntime();
        var pairingEvents = new ConcurrentQueue<RuntimePairingEvent>();
        var clientAppStateChangedEvents = new ConcurrentQueue<RuntimeClientAppStateChangedEvent>();
        var captureEvents = new ConcurrentQueue<RuntimeSessionCaptureEvent>();
        var transferEvents = new ConcurrentQueue<RuntimeSessionTransferEvent>();
        runtime.PairingEventOccurred += (_, runtimeEvent) => pairingEvents.Enqueue(runtimeEvent);
        runtime.ClientAppStateChanged += (_, runtimeEvent) => clientAppStateChangedEvents.Enqueue(runtimeEvent);
        runtime.SessionCaptureEventOccurred += (_, runtimeEvent) => captureEvents.Enqueue(runtimeEvent);
        runtime.SessionTransferEventOccurred += (_, runtimeEvent) => transferEvents.Enqueue(runtimeEvent);

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.Equal(ProtocolDefaults.WebSocketPath, connectResponse.WebSocketPath);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var appIconBytes = new byte[] { 9, 8, 7, 6 };
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_LOG",
            ["data"] = "Hello from client"
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_APP_STATE",
            ["state"] = "foreground",
            ["changedAtUtc"] = capturedAtUtc
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_METRIC_CHANNELS",
            ["channels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["name"] = "FPS",
                    ["color"] = "#00FF00",
                    ["unit"] = "fps",
                    ["type"] = "frames",
                    ["source"] = "reactNative",
                    ["group"] = "React Native",
                    ["kind"] = "react_native_js_fps"
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_METRICS",
            ["metrics"] = new JsonArray
            {
                new JsonObject
                {
                    ["channel"] = 1,
                    ["value"] = 60,
                    ["capturedAtUtc"] = capturedAtUtc
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_EVENTS",
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "evt-1",
                    ["label"] = "App moved to foreground",
                    ["eventType"] = "Lifecycle",
                    ["details"] = string.Empty,
                    ["capturedAtUtc"] = capturedAtUtc,
                    ["channel"] = 4
                },
                new JsonObject
                {
                    ["id"] = "evt-2",
                    ["label"] = "WorkspaceViewModel",
                    ["eventType"] = "Error",
                    ["details"] = "developer session log",
                    ["capturedAtUtc"] = capturedAtUtc.AddMilliseconds(10),
                    ["channel"] = 0
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_TOUCH_INPUT",
            ["schema"] = "ansight.touches.v1",
            ["t0"] = capturedAtUtc.AddMilliseconds(250),
            ["space"] = "w",
            ["unit"] = "pt",
            ["surface"] = new JsonArray(482, 480, 2),
            ["rows"] = new JsonArray
            {
                new JsonArray(0, 0, 1, 120.5, 240.25),
                new JsonArray(125, 2, 1, 122.5, 241.25)
            }
        });
        await SendJsonAsync(
            socket,
            JsonSerializer.SerializeToNode(
                new DeviceAppProfile
                {
                    Sdk = new DeviceSdkProfile
                    {
                        Name = "Ansight .NET SDK",
                        Version = "0.1.0-test",
                        Language = "dotnet"
                    },
                    Device = new DeviceProfile
                    {
                        Model = "iPhone 15",
                        OsName = "iOS",
                        IsEmulator = false
                    },
                    App = new DeviceApplicationProfile
                    {
                        AppId = pairingConfig.AppId,
                        AppName = pairingConfig.AppName,
                        VersionName = "1.0.0",
                        Icon = new DeviceApplicationIconProfile
                        {
                            Format = "png",
                            MimeType = "image/png",
                            Width = 2,
                            Height = 2,
                            ByteCount = appIconBytes.Length,
                            DataBase64 = Convert.ToBase64String(appIconBytes)
                        }
                    }
                },
                protocolJson)!.AsObject());
        await socket.SendAsync(
            CreateJpegPayload(capturedAtUtc.AddSeconds(1), 320, 200, 80, new byte[] { 1, 2, 3, 4, 5 }),
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_DONE",
            ["data"] = "done"
        });

        await DrainSocketAsync(socket);

        await TestWait.UntilAsync(
            () => pairingEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimePairingEventKind.DiscoveryReceived)
                  && pairingEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimePairingEventKind.PairingAccepted)
                  && captureEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Started)
                  && captureEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Stopped)
                  && clientAppStateChangedEvents.Any(runtimeEvent => runtimeEvent.CurrentState == AppLifecycleState.Foreground)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.Log)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.Telemetry)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.AppEvent)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.AppProfile)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.TouchInput)
                  && transferEvents.Any(runtimeEvent => runtimeEvent.Kind == RuntimeSessionTransferKind.Screenshot),
            because: "The full pairing and streaming event set should be emitted.");

        AppSessionSnapshot? snapshot = null;
        await TestWait.UntilAsync(
            () =>
            {
                var summary = runtime.Sessions.GetSummaries()
                    .FirstOrDefault(candidate => string.Equals(candidate.AppId, pairingConfig.AppId, StringComparison.Ordinal));
                if (summary is null || !runtime.Sessions.TryGetSnapshot(summary.SessionId, out snapshot))
                {
                    return false;
                }

                return snapshot is not null
                       && snapshot.Logs.Any(log => log.Message.Contains("Hello from client", StringComparison.Ordinal))
                       && snapshot.Logs.Any(log => string.Equals(log.Tag, "LIFECYCLE", StringComparison.Ordinal)
                                                   && log.Message.Contains("App moved to foreground", StringComparison.Ordinal))
                       && snapshot.MetricChannels.Any(channel => channel.ChannelId == 1
                                                                  && channel.Name == "FPS"
                                                                  && channel.Source == "reactNative"
                                                                  && channel.Group == "React Native"
                                                                  && channel.Kind == "react_native_js_fps")
                       && snapshot.Metrics.Any(metric => metric.ChannelId == 1 && metric.Value == 60)
                       && snapshot.DeviceProfile?.App?.AppName == pairingConfig.AppName
                       && snapshot.AppIcon is not null
                       && snapshot.Touches.Count == 2
                       && snapshot.Images.Count == 1
                       && snapshot.AppState == AppLifecycleState.Foreground
                       && snapshot.AppStateChangedUtc.HasValue;
            },
            timeout: TimeSpan.FromSeconds(15),
            because: "The runtime should persist the streamed session data.");

        Assert.NotNull(snapshot);
        Assert.Equal(pairingConfig.AppName, snapshot!.DeviceProfile?.App?.AppName);
        Assert.Equal("0.1.0-test", snapshot.SdkVersion);
        Assert.NotNull(snapshot.AppIcon);
        var sessionCapturesRootPath = SessionAppIconArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths);
        var appIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
            sessionCapturesRootPath,
            snapshot.AppId,
            snapshot.SessionId,
            snapshot.AppIcon!);
        Assert.True(File.Exists(appIconPath));
        Assert.Equal(appIconBytes, File.ReadAllBytes(appIconPath));
        Assert.Equal(AppLifecycleState.Foreground, snapshot.AppState);
        Assert.Contains(
            clientAppStateChangedEvents,
            runtimeEvent => runtimeEvent.PreviousState == AppLifecycleState.Unknown
                            && runtimeEvent.CurrentState == AppLifecycleState.Foreground
                            && runtimeEvent.SessionId == snapshot.SessionId);
        Assert.Contains(snapshot.Logs, log => log.Message.Contains("Hello from client", StringComparison.Ordinal));
        Assert.Contains(snapshot.Logs, log => string.Equals(log.Tag, "LIFECYCLE", StringComparison.Ordinal));
        Assert.Equal(["down", "up"], snapshot.Touches.Select(touch => touch.Action).ToArray());
        Assert.All(snapshot.Touches, touch => Assert.Equal("points", touch.CoordinateUnit));
        Assert.Contains(
            snapshot.Logs,
            log => string.Equals(log.Tag, "LIFECYCLE", StringComparison.Ordinal)
                   && string.Equals(log.Message, "App moved to foreground", StringComparison.Ordinal));
        Assert.Contains(
            snapshot.Logs,
            log => string.Equals(log.Tag, "ERROR", StringComparison.Ordinal)
                   && log.Priority == LogPriority.Error
                   && string.Equals(log.Message, "WorkspaceViewModel: developer session log", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.Logs,
            log => string.Equals(log.Tag, "LIFECYCLE", StringComparison.Ordinal)
                   && log.Message.Contains("ch:", StringComparison.Ordinal));
        Assert.Equal("WebSocket Closed", snapshot.Status);
        Assert.Empty(runtime.AppTools.GetConnectedSessionIds());
        Assert.DoesNotContain(
            captureEvents,
            runtimeEvent => runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Updated);

        var archivePath = Path.Combine(environment.RootPath, "session-export.ansight-session.zip");
        var exportResult = await runtime.SessionArchives.ExportSessionArchiveAsync(snapshot.SessionId, archivePath);
        Assert.True(exportResult.IsSuccess);
        using var archive = ZipFile.OpenRead(archivePath);
        var appIconEntry = archive.GetEntry(SessionAppIconArtifactPath.ResolveArchiveEntryPath(snapshot.AppIcon!));
        Assert.NotNull(appIconEntry);
        var sessionEntry = archive.GetEntry("session.json");
        Assert.NotNull(sessionEntry);
        var touchesEntry = archive.GetEntry("session-data/touches.json");
        Assert.NotNull(touchesEntry);
        using (var sessionStream = sessionEntry!.Open())
        using (var sessionDocument = await JsonDocument.ParseAsync(sessionStream))
        {
            var session = sessionDocument.RootElement.GetProperty("session");
            Assert.Equal("0.1.0-test", session.GetProperty("sdkVersion").GetString());
            Assert.Equal("0.1.0-test", session.GetProperty("deviceProfile").GetProperty("sdk").GetProperty("version").GetString());
        }
        using (var touchesStream = touchesEntry!.Open())
        using (var touchesDocument = await JsonDocument.ParseAsync(touchesStream))
        {
            Assert.Equal("ansight.touches.v1", touchesDocument.RootElement.GetProperty("schema").GetString());
            var batches = touchesDocument.RootElement.GetProperty("batches");
            var rows = batches[0].GetProperty("rows");
            Assert.Equal(2, rows.GetArrayLength());
            Assert.Equal(0, rows[0][1].GetInt32());
            Assert.Equal(2, rows[1][1].GetInt32());
        }

        using var appIconStream = appIconEntry!.Open();
        using var appIconBuffer = new MemoryStream();
        await appIconStream.CopyToAsync(appIconBuffer);
        Assert.Equal(appIconBytes, appIconBuffer.ToArray());
    }

    [Fact(Timeout = 60000)]
    public async Task PairingReconnect_WithSameProcessSessionId_ReusesSessionAndSplitsTelemetry()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.reconnect", "Reconnect App");
        using var runtime = environment.CreateRuntime();
        const string processSessionId = "process-session-001";

        await runtime.StartAsync();

        var firstResponse = await SendConnectRequestAsync(pairingConfig, processSessionId: processSessionId);
        Assert.True(firstResponse.Accepted);
        using var firstSocket = new ClientWebSocket();
        await firstSocket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{firstResponse.WebSocketPort}{firstResponse.WebSocketPath}?token={firstResponse.WebSocketToken}"),
            CancellationToken.None);

        string? firstSessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                var connectedSessions = runtime.AppTools.GetConnectedSessionIds();
                if (connectedSessions.Count != 1)
                {
                    return false;
                }

                firstSessionId = connectedSessions[0];
                return true;
            },
            because: "The first live session should be connected.");

        var firstCapturedAtUtc = DateTimeOffset.Parse("2026-03-28T06:21:43Z");
        await SendJsonAsync(firstSocket, new JsonObject
        {
            ["type"] = "CLIENT_METRIC_CHANNELS",
            ["channels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["name"] = "Native heap",
                    ["color"] = "#007AFF"
                }
            }
        });
        await SendJsonAsync(firstSocket, new JsonObject
        {
            ["type"] = "CLIENT_METRICS",
            ["metrics"] = new JsonArray
            {
                new JsonObject
                {
                    ["channel"] = 1,
                    ["value"] = 200,
                    ["capturedAtUtc"] = firstCapturedAtUtc
                }
            }
        });
        await SendJsonAsync(firstSocket, new JsonObject
        {
            ["type"] = "CLIENT_EVENTS",
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "evt-1",
                    ["label"] = "Foreground",
                    ["eventType"] = "Lifecycle",
                    ["details"] = string.Empty,
                    ["capturedAtUtc"] = firstCapturedAtUtc,
                    ["channel"] = 4
                }
            }
        });

        await firstSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "dropout", CancellationToken.None);
        await DrainSocketAsync(firstSocket);
        await TestWait.UntilAsync(
            () => runtime.AppTools.GetConnectedSessionIds().Count == 0,
            because: "The first connection should be fully closed before reconnecting.");

        var secondResponse = await SendConnectRequestAsync(pairingConfig, processSessionId: processSessionId);
        Assert.True(secondResponse.Accepted);
        using var secondSocket = new ClientWebSocket();
        await secondSocket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{secondResponse.WebSocketPort}{secondResponse.WebSocketPath}?token={secondResponse.WebSocketToken}"),
            CancellationToken.None);

        await TestWait.UntilAsync(
            () =>
            {
                var connectedSessions = runtime.AppTools.GetConnectedSessionIds();
                return connectedSessions.Count == 1
                       && string.Equals(connectedSessions[0], firstSessionId, StringComparison.Ordinal);
            },
            because: "Reconnect should reuse the original host session.");

        await SendJsonAsync(secondSocket, new JsonObject
        {
            ["type"] = "CLIENT_METRIC_CHANNELS",
            ["channels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["name"] = "Native heap",
                    ["color"] = "#007AFF"
                }
            }
        });
        await SendJsonAsync(secondSocket, new JsonObject
        {
            ["type"] = "CLIENT_METRICS",
            ["metrics"] = new JsonArray
            {
                new JsonObject
                {
                    ["channel"] = 1,
                    ["value"] = 200,
                    ["capturedAtUtc"] = firstCapturedAtUtc
                },
                new JsonObject
                {
                    ["channel"] = 1,
                    ["value"] = 260,
                    ["capturedAtUtc"] = firstCapturedAtUtc.AddSeconds(8)
                }
            }
        });
        await SendJsonAsync(secondSocket, new JsonObject
        {
            ["type"] = "CLIENT_EVENTS",
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "evt-1",
                    ["label"] = "Foreground",
                    ["eventType"] = "Lifecycle",
                    ["details"] = string.Empty,
                    ["capturedAtUtc"] = firstCapturedAtUtc,
                    ["channel"] = 4
                },
                new JsonObject
                {
                    ["id"] = "evt-2",
                    ["label"] = "Background",
                    ["eventType"] = "Lifecycle",
                    ["details"] = string.Empty,
                    ["capturedAtUtc"] = firstCapturedAtUtc.AddSeconds(8),
                    ["channel"] = 4
                }
            }
        });
        await SendJsonAsync(secondSocket, new JsonObject
        {
            ["type"] = "CLIENT_DONE",
            ["data"] = "done"
        });
        await DrainSocketAsync(secondSocket);

        AppSessionSnapshot? snapshot = null;
        await TestWait.UntilAsync(
            () =>
            {
                var summary = runtime.Sessions.GetSummaries()
                    .SingleOrDefault(candidate => string.Equals(candidate.SessionId, firstSessionId, StringComparison.Ordinal));
                if (summary is null || !runtime.Sessions.TryGetSnapshot(summary.SessionId, out snapshot))
                {
                    return false;
                }

                return snapshot is not null
                       && snapshot.ProcessSessionId == processSessionId
                       && snapshot.Metrics.Count == 2
                       && snapshot.Logs.Count(log => !string.IsNullOrWhiteSpace(log.EventId)) == 2;
            },
            timeout: TimeSpan.FromSeconds(15),
            because: "Reconnect data should append onto the original session without duplicating replayed entries.");

        Assert.NotNull(snapshot);
        Assert.Equal(processSessionId, snapshot!.ProcessSessionId);
        Assert.Equal(2, snapshot.Metrics.Count);
        Assert.Equal([1, 2], snapshot.Metrics.Select(metric => metric.SegmentId).Distinct().OrderBy(value => value).ToArray());
        Assert.Equal(["evt-1", "evt-2"], snapshot.Logs.Where(log => !string.IsNullOrWhiteSpace(log.EventId)).Select(log => log.EventId!).ToArray());
        Assert.Single(runtime.Sessions.GetSummaries(), candidate => string.Equals(candidate.AppId, pairingConfig.AppId, StringComparison.Ordinal));
    }

    [Fact(Timeout = 60000)]
    public async Task PairingAndStreamingFlow_WhenScreenshotPayloadIsFragmented_PersistsSessionImage()
    {
        using var environment = new TestEnvironment();
        var pairingConfig = environment.SeedPairingConfig("com.example.fragmented", "Fragmented Screenshot App");
        using var runtime = environment.CreateRuntime();

        await runtime.StartAsync();

        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted);
        Assert.NotNull(connectResponse.WebSocketPort);
        Assert.Equal(ProtocolDefaults.WebSocketPath, connectResponse.WebSocketPath);
        Assert.False(string.IsNullOrWhiteSpace(connectResponse.WebSocketToken));

        using var socket = new ClientWebSocket();
        var socketUri = new Uri(
            $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}");
        await socket.ConnectAsync(socketUri, CancellationToken.None);

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var imageBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var payload = CreateJpegPayload(capturedAtUtc, 320, 200, 80, imageBytes);

        await socket.SendAsync(payload.AsMemory(0, 9), WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(payload.AsMemory(9, 11), WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(payload.AsMemory(20), WebSocketMessageType.Binary, true, CancellationToken.None);
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_DONE",
            ["data"] = "done"
        });

        await DrainSocketAsync(socket);

        AppSessionSnapshot? snapshot = null;
        var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths);
        await TestWait.UntilAsync(
            () =>
            {
                snapshot = runtime.Sessions.GetSummaries()
                    .FirstOrDefault(candidate => string.Equals(candidate.AppId, pairingConfig.AppId, StringComparison.Ordinal));
                if (snapshot is null || snapshot.Images.Count != 1)
                {
                    return false;
                }

                var resolvedImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                    sessionCapturesRootPath,
                    pairingConfig.AppId,
                    snapshot.SessionId,
                    snapshot.Images[0]);
                return snapshot is not null
                       && File.Exists(resolvedImagePath);
            },
            timeout: TimeSpan.FromSeconds(15),
            because: "The host should persist fragmented screenshot payloads as session images.");

        Assert.NotNull(snapshot);
        var frame = Assert.Single(snapshot!.Images);
        Assert.Equal("jpeg", frame.Format);
        Assert.Equal(320, frame.Width);
        Assert.Equal(200, frame.Height);
        Assert.Equal(80, frame.Quality);
        Assert.Equal(imageBytes.Length, frame.ByteCount);
        var imagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            sessionCapturesRootPath,
            pairingConfig.AppId,
            snapshot.SessionId,
            frame);
        Assert.True(File.Exists(imagePath));
        Assert.Equal(imageBytes, File.ReadAllBytes(imagePath));
    }
}
