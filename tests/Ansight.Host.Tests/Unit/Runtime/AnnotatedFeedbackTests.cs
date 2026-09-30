using System.Buffers.Binary;
using System.IO.Compression;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AnnotatedFeedbackTests
{
    [Fact]
    public void BundleReader_MapsFeedbackShapesTreesArtifactsAndCustomData()
    {
        var bundleBytes = CreateBundle();
        using var stream = new MemoryStream(bundleBytes);

        var success = AnnotatedFeedbackBundleReader.TryRead(stream, "feedback.ansightannotation", out var content, out var error);

        Assert.True(success, error);
        Assert.NotNull(content);
        Assert.Equal("11111111111111111111111111111111", content.AnnotationId);
        Assert.Equal("The save button overlaps the footer.", content.Feedback);
        Assert.Equal("checkout", content.CustomData?["flow"]?.GetValue<string>());
        Assert.Single(content.VisualTrees);
        Assert.Single(content.Artifacts, artifact => artifact.Bytes is { Length: > 0 });

        var annotation = content.CreateAnnotation(content.ScreenshotFrameId);
        var expectedCaptureTime = new DateTimeOffset(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);
        Assert.Equal(expectedCaptureTime.AddSeconds(-1), annotation.StartUtc);
        Assert.Equal(expectedCaptureTime.AddSeconds(1), annotation.EndUtc);
        Assert.Equal(3, annotation.Geometry.Count);
        var geometry = Assert.Single(annotation.Geometry, item => item.Text == "Save action");
        Assert.Equal(SessionAnnotationGeometryKind.Ellipse, geometry.Kind);
        Assert.Equal(expectedCaptureTime, geometry.CapturedAtUtc);
        Assert.Equal("Save action", geometry.Text);
        Assert.Equal("#FFFF3B30", geometry.StrokeColor);
        Assert.Equal(3d, geometry.StrokeWidth);
        var rectanglePath = Assert.Single(annotation.Geometry, item => item.Text == "Footer overlap");
        Assert.Equal(SessionAnnotationGeometryKind.FreeDraw, rectanglePath.Kind);
        Assert.Equal(9, rectanglePath.Points.Count);
        var ellipsePath = Assert.Single(annotation.Geometry, item => item.Text == "Circular issue");
        Assert.Equal(SessionAnnotationGeometryKind.FreeDraw, ellipsePath.Kind);
        Assert.Equal(9, ellipsePath.Points.Count);

        var rectangleInference = Assert.IsType<JsonObject>(
            PayloadJson.BuildSessionAnnotationGeometryPayload(rectanglePath)["inferredShape"]);
        Assert.Equal("rectangle", rectangleInference["kind"]?.GetValue<string>());
        var rectangleBounds = Assert.IsType<JsonObject>(rectangleInference["bounds"]);
        Assert.Equal(0.15, rectangleBounds["x"]!.GetValue<double>(), precision: 6);
        Assert.Equal(0.3, rectangleBounds["width"]!.GetValue<double>(), precision: 6);
        var ellipseInference = Assert.IsType<JsonObject>(
            PayloadJson.BuildSessionAnnotationGeometryPayload(ellipsePath)["inferredShape"]);
        Assert.Equal("oval", ellipseInference["kind"]?.GetValue<string>());
        Assert.Null(PayloadJson.BuildSessionAnnotationGeometryPayload(geometry)["inferredShape"]);
        Assert.Equal("The save button overlaps the footer.", annotation.Notes);
        Assert.Single(annotation.Evidence, item => item.Kind == "artifact");

        var visualTree = Assert.Single(content.CreateVisualTreeSnapshots(content.ScreenshotFrameId));
        Assert.Equal(content.ScreenshotFrameId, visualTree.ScreenshotFrameId);
        Assert.Equal("native", visualTree.VisualTreeKind);
        Assert.NotNull(content.CreateArtifactSnapshot());
    }

    [Fact]
    public void AnnotationGeometryPayload_OmitsInferenceForOpenFreeDraw()
    {
        var geometry = new SessionAnnotationGeometry
        {
            GeometryId = "open-path",
            FrameId = "frame-1",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Kind = SessionAnnotationGeometryKind.FreeDraw,
            X = 0.1,
            Y = 0.1,
            Points =
            [
                new SessionAnnotationGeometryPoint { X = 0.1, Y = 0.1 },
                new SessionAnnotationGeometryPoint { X = 0.2, Y = 0.4 },
                new SessionAnnotationGeometryPoint { X = 0.5, Y = 0.2 },
                new SessionAnnotationGeometryPoint { X = 0.7, Y = 0.6 },
                new SessionAnnotationGeometryPoint { X = 0.9, Y = 0.8 }
            ]
        };

        var payload = PayloadJson.BuildSessionAnnotationGeometryPayload(geometry);

        Assert.Null(payload["inferredShape"]);
    }

    [Fact]
    public void AnnotationGeometryPayload_InfersLineFromStraightFreeDraw()
    {
        var geometry = new SessionAnnotationGeometry
        {
            GeometryId = "line-path",
            FrameId = "frame-1",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Kind = SessionAnnotationGeometryKind.FreeDraw,
            X = 0.1,
            Y = 0.2,
            Points =
            [
                new SessionAnnotationGeometryPoint { X = 0.1, Y = 0.2 },
                new SessionAnnotationGeometryPoint { X = 0.3, Y = 0.3 },
                new SessionAnnotationGeometryPoint { X = 0.5, Y = 0.4 },
                new SessionAnnotationGeometryPoint { X = 0.7, Y = 0.5 }
            ]
        };

        var payload = PayloadJson.BuildSessionAnnotationGeometryPayload(geometry);
        var inference = Assert.IsType<JsonObject>(payload["inferredShape"]);

        Assert.Equal("line", inference["kind"]?.GetValue<string>());
        Assert.Null(inference["focalPoint"]);
        Assert.True(inference["confidence"]?.GetValue<double>() > 0.99d);
    }

    [Fact]
    public void AnnotationGeometryPayload_InfersArrowAndItsFocalPointRegardlessOfStrokeDirection()
    {
        var geometry = new SessionAnnotationGeometry
        {
            GeometryId = "arrow-path",
            FrameId = "frame-1",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Kind = SessionAnnotationGeometryKind.FreeDraw,
            X = 0.1,
            Y = 0.42,
            Points =
            [
                new SessionAnnotationGeometryPoint { X = 0.1, Y = 0.5 },
                new SessionAnnotationGeometryPoint { X = 0.3, Y = 0.5 },
                new SessionAnnotationGeometryPoint { X = 0.5, Y = 0.5 },
                new SessionAnnotationGeometryPoint { X = 0.7, Y = 0.5 },
                new SessionAnnotationGeometryPoint { X = 0.62, Y = 0.42 },
                new SessionAnnotationGeometryPoint { X = 0.7, Y = 0.5 },
                new SessionAnnotationGeometryPoint { X = 0.62, Y = 0.58 }
            ]
        };

        var payload = PayloadJson.BuildSessionAnnotationGeometryPayload(geometry);
        var inference = Assert.IsType<JsonObject>(payload["inferredShape"]);
        var focalPoint = Assert.IsType<JsonObject>(inference["focalPoint"]);

        Assert.Equal("arrow", inference["kind"]?.GetValue<string>());
        Assert.Equal(0.7d, focalPoint["x"]!.GetValue<double>(), precision: 6);
        Assert.Equal(0.5d, focalPoint["y"]!.GetValue<double>(), precision: 6);

        var reversedGeometry = new SessionAnnotationGeometry
        {
            GeometryId = "reversed-arrow-path",
            FrameId = "frame-1",
            CapturedAtUtc = geometry.CapturedAtUtc,
            Kind = SessionAnnotationGeometryKind.FreeDraw,
            X = geometry.X,
            Y = geometry.Y,
            Points = geometry.Points.Reverse().ToArray()
        };
        var reversedPayload = PayloadJson.BuildSessionAnnotationGeometryPayload(reversedGeometry);
        var reversedInference = Assert.IsType<JsonObject>(reversedPayload["inferredShape"]);
        var reversedFocalPoint = Assert.IsType<JsonObject>(reversedInference["focalPoint"]);

        Assert.Equal("arrow", reversedInference["kind"]?.GetValue<string>());
        Assert.Equal(0.7d, reversedFocalPoint["x"]!.GetValue<double>(), precision: 6);
        Assert.Equal(0.5d, reversedFocalPoint["y"]!.GetValue<double>(), precision: 6);
    }

    [Fact]
    public void TransferManager_AcceptsDescriptorAndCompletesAsftTransfer()
    {
        var bundleBytes = CreateBundle();
        var transferId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using var manager = new AnnotatedFeedbackTransferManager();
        var payload = new JsonObject
        {
            ["schema"] = AnnotatedFeedbackTransferManager.SubmitSchema,
            ["clientAnnotationId"] = "11111111111111111111111111111111",
            ["capturedAtUtc"] = "2026-07-17T01:02:03Z",
            ["transfer"] = new JsonObject
            {
                ["transferId"] = transferId.ToString("N"),
                ["fileName"] = "feedback.ansightannotation",
                ["mimeType"] = AnnotatedFeedbackBundleReader.BundleMimeType,
                ["sizeBytes"] = bundleBytes.Length,
                ["wireProtocol"] = BinaryFileTransferProtocol.ProtocolName
            }
        };

        Assert.True(manager.TryRegister("session-1", payload, out var message), message);
        Assert.True(manager.TryHandleBinaryMessage(
            "session-1",
            CreateFrame(transferId, BinaryFileTransferFrameType.Chunk, 0, 0, bundleBytes),
            out var chunkCompletion));
        Assert.Null(chunkCompletion);
        Assert.True(manager.TryHandleBinaryMessage(
            "session-1",
            CreateFrame(transferId, BinaryFileTransferFrameType.Complete, 1, bundleBytes.Length, []),
            out var completion));

        Assert.NotNull(completion);
        try
        {
            Assert.Equal(bundleBytes, File.ReadAllBytes(completion.BundlePath));
            Assert.Equal("11111111111111111111111111111111", completion.ClientAnnotationId);
        }
        finally
        {
            File.Delete(completion.BundlePath);
        }
    }

    [Fact]
    public void OfflineCaptureArchive_ImportsAnnotatedFeedbackEvidence()
    {
        var bundleBytes = CreateBundle();
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "manifest.json", """
                {
                  "version": 1,
                  "sessionId": "offline-session",
                  "startedAtUtc": "2026-07-17T01:00:00Z",
                  "stoppedAtUtc": "2026-07-17T01:03:00Z",
                  "appId": "com.example.app",
                  "clientName": "Example App"
                }
                """);
            var bundleEntry = archive.CreateEntry("annotations/bundles/feedback.ansightannotation");
            using var output = bundleEntry.Open();
            output.Write(bundleBytes);
        }

        archiveStream.Position = 0;
        using var inputArchive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var success = SessionArchiveExternalPayloadReader.TryReadJsonLinesArchive(
            inputArchive,
            "offline.ansight",
            out var payload,
            out var error);

        Assert.True(success, error);
        Assert.NotNull(payload);
        var annotation = Assert.Single(payload.Snapshot.Annotations);
        Assert.Equal("sdk.annotatedFeedback", annotation.Source);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 1, 2, 2, TimeSpan.Zero), annotation.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 1, 2, 4, TimeSpan.Zero), annotation.EndUtc);
        Assert.Equal(3, annotation.Geometry.Count);
        Assert.Equal(2, annotation.Geometry.Count(item => item.Kind == SessionAnnotationGeometryKind.FreeDraw));
        Assert.Single(annotation.Geometry, item => item.Kind == SessionAnnotationGeometryKind.Ellipse);
        Assert.Single(payload.Snapshot.Images);
        Assert.Single(payload.Snapshot.VisualTreeSnapshots);
        Assert.Single(payload.Snapshot.ArtifactSnapshots);
        Assert.Single(payload.ArtifactBytesByRelativePath);
    }

    private static byte[] CreateBundle()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteBytesEntry(archive, "evidence/screenshot.jpg", [1, 2, 3, 4]);
            WriteTextEntry(archive, "evidence/visual-trees/native.json", """
                {
                  "schema": "ansight.native.visual-tree.compact.v2",
                  "kind": "native",
                  "platform": "ios",
                  "rootScope": "window",
                  "nodeCount": 2,
                  "root": { "type": "UIView" }
                }
                """);
            WriteTextEntry(archive, "artifacts/000-diagnostics.txt", "diagnostics");
            WriteTextEntry(archive, "manifest.json", """
                {
                  "schema": "ansight.annotation.bundle.v1",
                  "version": 1,
                  "annotationId": "11111111-1111-1111-1111-111111111111",
                  "captureGroupId": "33333333-3333-3333-3333-333333333333",
                  "capturedAtUtc": "2026-07-17T01:02:03Z",
                  "feedback": "The save button overlaps the footer.",
                  "shapes": [
                    {
                      "kind": "ellipse",
                      "x": 0.1,
                      "y": 0.2,
                      "width": 0.3,
                      "height": 0.4,
                      "text": "Save action",
                      "strokeColor": "#FFFF3B30",
                      "strokeWidth": 3
                    },
                    {
                      "kind": "freeDraw",
                      "x": 0.15,
                      "y": 0.3,
                      "width": 0.3,
                      "height": 0.2,
                      "text": "Footer overlap",
                      "points": [
                        { "x": 0.15, "y": 0.3 },
                        { "x": 0.3, "y": 0.3 },
                        { "x": 0.45, "y": 0.3 },
                        { "x": 0.45, "y": 0.4 },
                        { "x": 0.45, "y": 0.5 },
                        { "x": 0.3, "y": 0.5 },
                        { "x": 0.15, "y": 0.5 },
                        { "x": 0.15, "y": 0.4 },
                        { "x": 0.15, "y": 0.3 }
                      ],
                      "strokeColor": "#FFFF3B30",
                      "strokeWidth": 3
                    },
                    {
                      "kind": "freeDraw",
                      "x": 0.55,
                      "y": 0.3,
                      "width": 0.3,
                      "height": 0.2,
                      "text": "Circular issue",
                      "points": [
                        { "x": 0.7, "y": 0.3 },
                        { "x": 0.806, "y": 0.3293 },
                        { "x": 0.85, "y": 0.4 },
                        { "x": 0.806, "y": 0.4707 },
                        { "x": 0.7, "y": 0.5 },
                        { "x": 0.594, "y": 0.4707 },
                        { "x": 0.55, "y": 0.4 },
                        { "x": 0.594, "y": 0.3293 },
                        { "x": 0.7, "y": 0.3 }
                      ],
                      "strokeColor": "#FFFF3B30",
                      "strokeWidth": 3
                    }
                  ],
                  "screenshot": {
                    "path": "evidence/screenshot.jpg",
                    "mimeType": "image/jpeg",
                    "width": 400,
                    "height": 800,
                    "capturedAtUtc": "2026-07-17T01:02:03Z"
                  },
                  "visualTrees": [
                    {
                      "source": "native",
                      "displayName": "Native",
                      "path": "evidence/visual-trees/native.json",
                      "capturedAtUtc": "2026-07-17T01:02:03Z",
                      "truncated": false
                    }
                  ],
                  "evidence": [
                    {
                      "id": "screenshot",
                      "kind": "screenshot",
                      "status": "captured",
                      "capturedAtUtc": "2026-07-17T01:02:03Z",
                      "sizeBytes": 4
                    }
                  ],
                  "customData": { "flow": "checkout" },
                  "hookFailures": [ "ExampleHook: unavailable" ],
                  "artifacts": [
                    {
                      "name": "Diagnostics",
                      "kind": "log",
                      "mimeType": "text/plain",
                      "fileName": "diagnostics.txt",
                      "status": "captured",
                      "path": "artifacts/000-diagnostics.txt",
                      "sizeBytes": 11
                    }
                  ]
                }
                """);
        }

        return stream.ToArray();
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string value)
        => WriteBytesEntry(archive, path, Encoding.UTF8.GetBytes(value));

    private static void WriteBytesEntry(ZipArchive archive, string path, byte[] value)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(value);
    }

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
        Encoding.ASCII.GetBytes(transferId.ToString("N")).CopyTo(frame, 8);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40, 4), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(44, 8), offsetBytes);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(52, 4), payload.Length);
        payload.CopyTo(frame, BinaryFileTransferProtocol.HeaderSize);
        return frame;
    }
}
