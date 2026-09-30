using System.Buffers.Binary;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class BinaryToolArtifactTransferManagerTests
{
    [Fact]
    public async Task TinyJsonCompletedBeforeCallerWait_RemainsAvailableForOneSnapshotCapture()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var transferId = Guid.NewGuid();
        var envelope = CreateJsonArtifactResponse(sessionId, transferId);
        var registered = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", envelope));
        try
        {
            CompleteJsonTransfer(manager, sessionId, transferId);
            await registered.Completion;

            // Reproduces the fast JSON path: the receiver processed every frame before
            // SendToolRequestAsync had a chance to look up the completion registration.
            Assert.Empty(manager.TryGetCompletions("a-different-session", envelope));
            var claimed = Assert.Single(manager.TryGetCompletions(sessionId, envelope));
            Assert.True(claimed.CaptureSessionArtifactSnapshot);
            Assert.Equal("run-result", claimed.ArtifactId);
            Assert.True(claimed.Completion.IsCompletedSuccessfully);
            Assert.Equal("{}", await File.ReadAllTextAsync(claimed.ArtifactPath));
            Assert.Empty(manager.TryGetCompletions(sessionId, envelope));
        }
        finally
        {
            DeleteArtifactDirectory(registered);
        }
    }

    [Fact]
    public async Task ClaimedBeforeCompletion_CannotBeClaimedAgainAfterCompletion()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var transferId = Guid.NewGuid();
        var envelope = CreateJsonArtifactResponse(sessionId, transferId);
        var registered = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", envelope));
        try
        {
            var claimed = Assert.Single(manager.TryGetCompletions(sessionId, envelope));
            Assert.False(claimed.Completion.IsCompleted);
            Assert.Empty(manager.TryGetCompletions(sessionId, envelope));
            CompleteJsonTransfer(manager, sessionId, transferId);
            await claimed.Completion;
            Assert.Empty(manager.TryGetCompletions(sessionId, envelope));
        }
        finally
        {
            DeleteArtifactDirectory(registered);
        }
    }

    [Fact]
    public async Task FailedBeforeCallerWait_PreservesFailureInsteadOfSkippingTransfer()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var transferId = Guid.NewGuid();
        var envelope = CreateJsonArtifactResponse(sessionId, transferId);
        var registered = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", envelope));
        try
        {
            Assert.True(manager.TryHandleBinaryMessage(sessionId,
                CreateFrame(transferId, BinaryFileTransferFrameType.Error, 0, 0, Encoding.UTF8.GetBytes("transfer rejected"))));
            var claimed = Assert.Single(manager.TryGetCompletions(sessionId, envelope));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => claimed.Completion);
            Assert.Equal("transfer rejected", error.Message);
            Assert.False(File.Exists(claimed.ArtifactPath));
            Assert.Empty(manager.TryGetCompletions(sessionId, envelope));
        }
        finally
        {
            DeleteArtifactDirectory(registered);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelResponse_ReleasesPendingCompletionAndStopsAnActiveTransfer(bool completed)
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var transferId = Guid.NewGuid();
        var envelope = CreateJsonArtifactResponse(sessionId, transferId);
        var registered = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", envelope));
        try
        {
            if (completed) CompleteJsonTransfer(manager, sessionId, transferId);
            manager.CancelResponse(sessionId, envelope, "request cancelled");
            Assert.Empty(manager.TryGetCompletions(sessionId, envelope));
            if (completed)
            {
                await registered.Completion;
                Assert.True(File.Exists(registered.ArtifactPath));
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => registered.Completion);
                Assert.False(File.Exists(registered.ArtifactPath));
                Assert.False(manager.TryHandleBinaryMessage(sessionId,
                    CreateFrame(transferId, BinaryFileTransferFrameType.Chunk, 0, 0, [1, 2])));
            }
        }
        finally
        {
            DeleteArtifactDirectory(registered);
        }
    }

    [Fact]
    public async Task CancelSession_ReleasesCompletedAndClaimedTransfersWithoutAffectingAnotherSession()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var otherSessionId = $"sess_{Guid.NewGuid():N}";
        var completedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var completedResponse = CreateJsonArtifactResponse(sessionId, completedId);
        var activeResponse = CreateJsonArtifactResponse(sessionId, activeId);
        var otherResponse = CreateJsonArtifactResponse(otherSessionId, otherId);
        var completed = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", completedResponse));
        var active = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(sessionId, "app_1", activeResponse));
        var other = Assert.IsType<BinaryToolArtifactTransferRegistration>(manager.TryRegister(otherSessionId, "app_1", otherResponse));
        try
        {
            CompleteJsonTransfer(manager, sessionId, completedId);
            Assert.Single(manager.TryGetCompletions(sessionId, activeResponse));
            manager.CancelSession(sessionId, "session closed");
            await completed.Completion;
            await Assert.ThrowsAsync<OperationCanceledException>(() => active.Completion);
            Assert.Empty(manager.TryGetCompletions(sessionId, completedResponse));
            Assert.Empty(manager.TryGetCompletions(sessionId, activeResponse));
            Assert.False(File.Exists(active.ArtifactPath));
            Assert.Single(manager.TryGetCompletions(otherSessionId, otherResponse));
            CompleteJsonTransfer(manager, otherSessionId, otherId);
            await other.Completion;
        }
        finally
        {
            DeleteArtifactDirectory(completed);
            DeleteArtifactDirectory(active);
            DeleteArtifactDirectory(other);
        }
    }

    [Fact]
    public async Task ArtifactsRequest_BinaryFrames_CompletesLocalArtifact()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var transferId = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var envelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.ResultType,
            Id = "req_1.response",
            ReplyTo = "req_1",
            SessionId = "sess_1",
            Payload = new JsonObject
            {
                ["toolId"] = "artifacts.request",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["artifactId"] = "diagnostics",
                        ["providerId"] = "debug",
                        ["name"] = "Diagnostics",
                        ["kind"] = "log",
                        ["mimeType"] = "text/plain",
                        ["fileName"] = "diagnostics.txt",
                        ["sizeBytes"] = 5,
                        ["createdAtUtc"] = "2026-06-02T00:00:00.0000000Z"
                    },
                    ["downloadId"] = "download_1",
                    ["transferId"] = transferId.ToString("N"),
                    ["deliveryMode"] = "websocket_binary",
                    ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName,
                    ["status"] = "queued"
                }
            }
        };

        var registration = manager.TryRegister("sess_1", "app_1", envelope);

        try
        {
            Assert.NotNull(registration);
            Assert.Equal("artifacts.request", registration.ToolId);
            Assert.Equal("debug", registration.ProviderId);
            Assert.Equal("diagnostics", registration.ArtifactId);
            Assert.Equal("Diagnostics", registration.Name);
            Assert.Equal("log", registration.Kind);
            Assert.Equal("text/plain", registration.MimeType);
            Assert.Equal("diagnostics.txt", registration.FileName);
            Assert.True(registration.CaptureSessionArtifactSnapshot);

            var payload = Assert.IsType<JsonObject>(envelope.Payload);
            var result = Assert.IsType<JsonObject>(payload["result"]);
            Assert.Equal("receiving", result["status"]?.GetValue<string>());
            Assert.Equal("log", result["artifactKind"]?.GetValue<string>());
            Assert.Equal("text/plain", result["mimeType"]?.GetValue<string>());
            Assert.Equal(registration.ArtifactPath, result["artifactPath"]?.GetValue<string>());

            Assert.True(manager.TryGetCompletion("sess_1", envelope, out var completionRegistration));
            Assert.Equal(registration.TransferId, completionRegistration.TransferId);

            Assert.True(manager.TryHandleBinaryMessage(
                "sess_1",
                CreateFrame(transferId, BinaryFileTransferFrameType.Chunk, sequence: 0, offsetBytes: 0, payload: [1, 2, 3, 4, 5])));
            Assert.True(manager.TryHandleBinaryMessage(
                "sess_1",
                CreateFrame(transferId, BinaryFileTransferFrameType.Complete, sequence: 1, offsetBytes: 5, payload: [])));

            await registration.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("complete", result["status"]?.GetValue<string>());
            Assert.Equal(5, result["receivedBytes"]?.GetValue<long>());
            Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(registration.ArtifactPath));
        }
        finally
        {
            if (registration is not null)
            {
                var directoryPath = Path.GetDirectoryName(registration.ArtifactPath);
                if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task FilesBeginBinaryDownload_BinaryFrames_CompletesRetainedArtifact()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var transferId = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");
        var envelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.ResultType,
            Id = "req_file.response",
            ReplyTo = "req_file",
            SessionId = "sess_1",
            Payload = new JsonObject
            {
                ["toolId"] = "files.begin_binary_download",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["rootAlias"] = "appData",
                    ["relativePath"] = "ansight/offline-sync/data.db3",
                    ["fileName"] = "data.db3",
                    ["mimeType"] = "application/vnd.sqlite3",
                    ["sizeBytes"] = 4,
                    ["transferId"] = transferId.ToString("N"),
                    ["deliveryMode"] = "websocket_binary",
                    ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName,
                    ["status"] = "queued",
                    ["capturedAtUtc"] = "2026-09-05T00:00:00.0000000Z"
                }
            }
        };

        var registration = manager.TryRegister("sess_1", "app_1", envelope);

        try
        {
            Assert.NotNull(registration);
            Assert.Equal("files.begin_binary_download", registration.ToolId);
            Assert.Equal("files", registration.ProviderId);
            Assert.Equal("ansight/offline-sync/data.db3", registration.ArtifactId);
            Assert.Equal("data", registration.Name);
            Assert.Equal("file", registration.Kind);
            Assert.Equal("application/vnd.sqlite3", registration.MimeType);
            Assert.Equal("data.db3", registration.FileName);
            Assert.Equal(DateTimeOffset.Parse("2026-09-05T00:00:00.0000000Z"), registration.CapturedAtUtc);
            Assert.True(registration.CaptureSessionArtifactSnapshot);

            var payload = Assert.IsType<JsonObject>(envelope.Payload);
            var result = Assert.IsType<JsonObject>(payload["result"]);
            Assert.Equal("receiving", result["status"]?.GetValue<string>());
            Assert.Equal("file", result["artifactKind"]?.GetValue<string>());
            Assert.Equal(registration.ArtifactPath, result["artifactPath"]?.GetValue<string>());

            Assert.True(manager.TryGetCompletion("sess_1", envelope, out var completionRegistration));
            Assert.Equal(registration.TransferId, completionRegistration.TransferId);
            Assert.True(completionRegistration.CaptureSessionArtifactSnapshot);

            Assert.True(manager.TryHandleBinaryMessage(
                "sess_1",
                CreateFrame(transferId, BinaryFileTransferFrameType.Chunk, sequence: 0, offsetBytes: 0, payload: [1, 2, 3, 4])));
            Assert.True(manager.TryHandleBinaryMessage(
                "sess_1",
                CreateFrame(transferId, BinaryFileTransferFrameType.Complete, sequence: 1, offsetBytes: 4, payload: [])));

            await registration.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("complete", result["status"]?.GetValue<string>());
            Assert.Equal(4, result["receivedBytes"]?.GetValue<long>());
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(registration.ArtifactPath));
        }
        finally
        {
            if (registration is not null)
            {
                var directoryPath = Path.GetDirectoryName(registration.ArtifactPath);
                if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void BatchEvidence_RegistersEveryNestedScreenshotTransfer()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var firstTransferId = Guid.NewGuid();
        var secondTransferId = Guid.NewGuid();
        var envelope = new ToolProtocolEnvelope
        {
            Type = "tool.batch.result",
            Id = "batch_1.response",
            ReplyTo = "batch_1",
            SessionId = "sess_1",
            Payload = new JsonObject
            {
                ["success"] = true,
                ["results"] = new JsonArray
                {
                    CreateEvidenceResult("first", firstTransferId),
                    CreateEvidenceResult("second", secondTransferId)
                }
            }
        };

        var registrations = manager.TryRegisterAll("sess_1", "app_1", envelope);
        try
        {
            Assert.Equal(2, registrations.Count);
            Assert.Equal(
                [firstTransferId.ToString("N"), secondTransferId.ToString("N")],
                registrations.Select(static registration => registration.TransferId));
            Assert.Equal(2, manager.TryGetCompletions("sess_1", envelope).Count);
        }
        finally
        {
            manager.Dispose();
            foreach (var directoryPath in registrations
                         .Select(static registration => Path.GetDirectoryName(registration.ArtifactPath))
                         .Where(static path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.Ordinal))
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void ToolCatalogDefinitions_WithSchemaPropertiesNamedToolId_AreIgnored()
    {
        using var manager = new BinaryToolArtifactTransferManager();
        var envelope = new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.CatalogType,
            Id = "catalog_1.response",
            ReplyTo = "catalog_1",
            SessionId = "sess_1",
            Payload = new JsonObject
            {
                ["detail"] = "definitions",
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "ui.perform_action",
                        ["policy"] = "write",
                        ["argumentsSchema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["toolId"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        }
                    }
                }
            }
        };

        var registrations = manager.TryRegisterAll("sess_1", "app_1", envelope);

        Assert.Empty(registrations);
    }

    private static ToolProtocolEnvelope CreateJsonArtifactResponse(string sessionId, Guid transferId)
        => new()
        {
            Type = ToolProtocolMessageTypes.ResultType,
            Id = $"{transferId:N}.response",
            ReplyTo = transferId.ToString("N"),
            SessionId = sessionId,
            Payload = new JsonObject
            {
                ["toolId"] = "artifacts.request",
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["artifactId"] = "run-result",
                        ["providerId"] = "audio-harness.runs",
                        ["name"] = "Audio run result",
                        ["kind"] = "report",
                        ["mimeType"] = "application/json",
                        ["fileName"] = "result.json",
                        ["sizeBytes"] = 2
                    },
                    ["transferId"] = transferId.ToString("N"),
                    ["deliveryMode"] = "websocket_binary",
                    ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName,
                    ["status"] = "queued"
                }
            }
        };

    private static void CompleteJsonTransfer(BinaryToolArtifactTransferManager manager, string sessionId, Guid transferId)
    {
        Assert.True(manager.TryHandleBinaryMessage(sessionId,
            CreateFrame(transferId, BinaryFileTransferFrameType.Chunk, 0, 0, Encoding.UTF8.GetBytes("{}"))));
        Assert.True(manager.TryHandleBinaryMessage(sessionId,
            CreateFrame(transferId, BinaryFileTransferFrameType.Complete, 1, 2, [])));
    }

    private static void DeleteArtifactDirectory(BinaryToolArtifactTransferRegistration registration)
    {
        var directory = Path.GetDirectoryName(registration.ArtifactPath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static JsonObject CreateEvidenceResult(string callId, Guid transferId)
        => new()
        {
            ["callId"] = callId,
            ["toolId"] = "ui.perform_action",
            ["success"] = true,
            ["evidence"] = new JsonObject
            {
                ["screenshot"] = new JsonObject
                {
                    ["toolId"] = "ui.get_screenshot",
                    ["success"] = true,
                    ["result"] = new JsonObject
                    {
                        ["transferId"] = transferId.ToString("N"),
                        ["deliveryMode"] = "websocket_binary",
                        ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName,
                        ["sizeBytes"] = 1,
                        ["format"] = "jpeg",
                        ["mimeType"] = "image/jpeg",
                        ["fileName"] = $"{callId}.jpg"
                    }
                }
            }
        };

    private static byte[] CreateFrame(
        Guid transferId,
        BinaryFileTransferFrameType frameType,
        int sequence,
        long offsetBytes,
        byte[] payload)
    {
        var frame = new byte[BinaryFileTransferProtocol.HeaderSize + payload.Length];
        frame[0] = (byte)'A';
        frame[1] = (byte)'S';
        frame[2] = (byte)'F';
        frame[3] = (byte)'T';
        frame[4] = 1;
        frame[5] = (byte)frameType;
        frame[6] = 0;
        frame[7] = 0;
        Encoding.ASCII.GetBytes(transferId.ToString("N")).CopyTo(frame, 8);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(44, 8), offsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(52, 4), payload.Length);
        payload.CopyTo(frame, BinaryFileTransferProtocol.HeaderSize);
        return frame;
    }
}
