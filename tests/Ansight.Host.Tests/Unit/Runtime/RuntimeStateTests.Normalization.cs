using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
    [Fact]
    public void NormalizeSession_RemovesSequentialScreenshotsAndPerTypeVisualTreesAndRemapsReferences()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-08-28T01:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);

        var frames = new[]
        {
            CreateImageFrame("frame-001", At(1)),
            CreateImageFrame("frame-002", At(2)),
            CreateImageFrame("frame-003", At(3)),
            CreateImageFrame("frame-004", At(4))
        };
        var visualTrees = new[]
        {
            CreateTypedVisualTree("ios-001", At(1), "ios.native", "native.v2", "ios", "window", "unchanged"),
            CreateTypedVisualTree("maui-001", At(2), "maui", "maui.v1", "ios", "page", "unchanged", "frame-002"),
            CreateTypedVisualTree("ios-002", At(3), "ios.native", "native.v2", "ios", "window", "unchanged"),
            CreateTypedVisualTree("ios-003", At(4), "ios.native", "native.v2", "ios", "window", "changed"),
            CreateTypedVisualTree("maui-002", At(5), "maui", "maui.v1", "ios", "page", "unchanged"),
            CreateTypedVisualTree("ios-004", At(6), "ios.native", "native.v2", "ios", "window", "unchanged")
        };
        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "normalize-001",
                AppId = "com.example.normalize",
                ClientName = "Imported Client",
                RemoteAddress = "127.0.0.1",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(10),
                IsHistorical = true,
                Images = frames,
                VisualTreeSnapshots = visualTrees,
                Annotations =
                [
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-001",
                        StartUtc = At(2),
                        Label = "Duplicate evidence references",
                        Geometry = [CreateAnnotationGeometry("geometry-001", "frame-002", At(2))],
                        Target = new SessionAnnotationTarget
                        {
                            Kind = "visualTreeElement",
                            TargetId = "target-001",
                            VisualTreeSnapshotId = "ios-002"
                        }
                    }
                ],
                AgentTaskLinks =
                [
                    new SessionAgentTaskLink
                    {
                        LinkId = "link-001",
                        SessionId = "normalize-001",
                        AnnotationBatchId = "batch-001",
                        FrameIds = ["frame-001", "frame-002"],
                        Source = "test",
                        Provider = "codex",
                        ProviderTaskId = "task-001",
                        SubmittedPrompt = "Review the screenshots.",
                        Status = "complete",
                        CreatedAtUtc = At(2),
                        UpdatedAtUtc = At(2)
                    }
                ],
                MetricChannels = [],
                Metrics = []
            },
            new Dictionary<string, byte[]>
            {
                ["frame-001"] = [1, 2, 3],
                ["frame-002"] = [1, 2, 3],
                ["frame-003"] = [7, 8, 9],
                ["frame-004"] = [1, 2, 3]
            });

        Assert.True(importResult.IsSuccess);
        var sessionId = importResult.ImportedSession!.SessionId;
        var duplicateImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths),
            importResult.ImportedSession.AppId,
            sessionId,
            frames[1]);
        Assert.True(File.Exists(duplicateImagePath));

        var progress = new List<SessionOptimizationProgress>();
        var result = runtimeState.NormalizeSession(sessionId, new SessionOptimizationOptions(), progress.Add);

        Assert.True(result.IsSuccess);
        Assert.Contains(progress, item => item.Message == "Checking screenshots for duplicates…" && item.Completed == 0 && item.Total == 4);
        Assert.Contains(progress, item => item.Message == "Checking screenshots for duplicates…" && item.Completed == 4 && item.Total == 4);
        Assert.Contains(progress, item => item.Message == "Checking visual trees for duplicates…" && item.Completed == 0 && item.Total == 6);
        Assert.Contains(progress, item => item.Message == "Checking visual trees for duplicates…" && item.Completed == 6 && item.Total == 6);
        Assert.Contains(progress, item => item.Message == "Saving deduplicated session evidence…");
        Assert.Equal(1, result.RemovedScreenshotCount);
        Assert.Equal(2, result.RemovedVisualTreeSnapshotCount);
        Assert.Equal(3, result.RemovedItemCount);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var normalized));
        Assert.NotNull(normalized);
        Assert.Equal(
            ["frame-001", "frame-003", "frame-004"],
            normalized!.Images.Select(static frame => frame.FrameId).ToArray());
        Assert.Equal(
            ["ios-001", "maui-001", "ios-003", "ios-004"],
            normalized.VisualTreeSnapshots.Select(static tree => tree.SnapshotId).ToArray());
        Assert.Equal(
            "frame-001",
            normalized.VisualTreeSnapshots.Single(tree => tree.SnapshotId == "maui-001").ScreenshotFrameId);
        var annotation = Assert.Single(normalized.Annotations);
        Assert.Equal("frame-001", Assert.Single(annotation.Geometry).FrameId);
        Assert.Equal("ios-001", annotation.Target?.VisualTreeSnapshotId);
        Assert.Equal(["frame-001"], Assert.Single(normalized.AgentTaskLinks).FrameIds);

        Assert.True(captureStore.TryLoad(sessionId, out var persisted));
        Assert.NotNull(persisted);
        Assert.Equal(3, persisted!.Images.Count);
        Assert.Equal(4, persisted.VisualTreeSnapshots.Count);
        Assert.False(File.Exists(duplicateImagePath));

        var secondResult = runtimeState.NormalizeSession(sessionId);
        Assert.True(secondResult.IsSuccess);
        Assert.Equal(0, secondResult.RemovedItemCount);
    }

    private static SessionVisualTreeSnapshot CreateTypedVisualTree(
        string snapshotId,
        DateTimeOffset capturedAtUtc,
        string kind,
        string format,
        string platform,
        string rootScope,
        string value,
        string? screenshotFrameId = null)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            VisualTreeKind = kind,
            VisualTreeFormat = format,
            RuntimePlatform = platform,
            Source = "test",
            RootScope = rootScope,
            NodeCount = 1,
            ScreenshotFrameId = screenshotFrameId,
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["value"] = value
            }
        };
    }
}
