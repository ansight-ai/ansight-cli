using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
    [Fact]
    public async Task TrimSessionTimeline_KeepSelection_FiltersTimelineDataAndClampsAnnotations()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var frames = new[]
        {
            CreateImageFrame("frame-005", At(5)),
            CreateImageFrame("frame-015", At(15)),
            CreateImageFrame("frame-025", At(25))
        };

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-trim-001",
                AppId = "com.example.trim",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(40),
                IsHistorical = true,
                AppState = AppLifecycleState.Background,
                AppStateChangedUtc = At(30),
                Images = frames,
                VisualTreeSnapshots =
                [
                    CreateVisualTreeSnapshot("visual-015", At(15)),
                    CreateVisualTreeSnapshot("visual-025", At(25))
                ],
                ArtifactSnapshots =
                [
                    CreateArtifactSnapshot("artifact-015", At(15)),
                    CreateArtifactSnapshot("artifact-025", At(25))
                ],
                ApplicationEvents =
                [
                    new SessionApplicationEvent("app-005", "Early", "Debug", string.Empty, At(5), 255),
                    new SessionApplicationEvent("app-015", "Inside", "Debug", string.Empty, At(15), 255),
                    new SessionApplicationEvent("app-025", "Late", "Debug", string.Empty, At(25), 255)
                ],
                Analyses =
                [
                    new SessionAnalysisRecord
                    {
                        AnalysisId = "analysis-001",
                        AgentId = "codex",
                        StartedUtc = At(30),
                        CompletedUtc = At(35),
                        Success = true
                    }
                ],
                Annotations =
                [
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-spanning",
                        StartUtc = At(5),
                        EndUtc = At(25),
                        Label = "Spanning annotation",
                        Geometry =
                        [
                            CreateAnnotationGeometry("geometry-015", "frame-015", At(15)),
                            CreateAnnotationGeometry("geometry-025", "frame-025", At(25))
                        ]
                    },
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-outside",
                        StartUtc = At(25),
                        EndUtc = At(30),
                        Label = "Outside annotation"
                    }
                ],
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                Touches =
                [
                    CreateTouch("touch-005", At(5)),
                    CreateTouch("touch-015", At(15)),
                    CreateTouch("touch-025", At(25))
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            frames.ToDictionary(frame => frame.FrameId, _ => new byte[] { 1, 2, 3 }),
            artifactBytesByRelativePath: new Dictionary<string, byte[]>
            {
                ["artifact-015/file.txt"] = [4, 5, 6],
                ["artifact-025/file.txt"] = [7, 8, 9]
            });

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);
        var importedSession = importResult.ImportedSession!;
        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var sessionDirectoryPath = SessionImageArtifactPath.ResolveSessionDirectoryPath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId);
        var removedEarlyImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId,
            frames[0]);
        var keptImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId,
            frames[1]);
        var removedLateImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId,
            frames[2]);
        var keptArtifactDirectoryPath = Path.Combine(sessionDirectoryPath, "artifacts", "artifact-015");
        var removedArtifactDirectoryPath = Path.Combine(sessionDirectoryPath, "artifacts", "artifact-025");
        Assert.True(File.Exists(removedEarlyImagePath));
        Assert.True(File.Exists(keptImagePath));
        Assert.True(File.Exists(removedLateImagePath));
        Assert.True(Directory.Exists(keptArtifactDirectoryPath));
        Assert.True(Directory.Exists(removedArtifactDirectoryPath));

        var progress = new List<SessionTimelineTrimProgress>();
        var result = runtimeState.TrimSessionTimeline(
            importedSession.SessionId,
            At(10),
            At(20),
            SessionTimelineTrimMode.KeepSelectionOnly,
            progress.Add);

        Assert.True(result.IsSuccess);
        Assert.Contains(progress, item => item.Message == "Filtering logs…" && item.Completed == 3 && item.Total == 3);
        Assert.Contains(progress, item => item.Message == "Removing detached evidence files…");
        Assert.Contains(progress, item => item.Message == "Saving retained logs…");
        Assert.Contains(progress, item => item.Message == "Saving retained telemetry…");
        Assert.True(runtimeState.TryGetSessionSnapshot(importedSession.SessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(At(10), snapshot!.CreatedUtc);
        Assert.Equal(At(20), snapshot.LastUpdatedUtc);
        Assert.Null(snapshot.AppStateChangedUtc);
        Assert.Equal([At(15)], snapshot!.Logs.Select(log => log.TimestampUtc).ToArray());
        Assert.Equal(["touch-015"], snapshot.Touches.Select(touch => touch.Id).ToArray());
        Assert.Equal([15], snapshot.Metrics.Select(metric => metric.Value).ToArray());
        Assert.Equal(["frame-015"], snapshot.Images.Select(frame => frame.FrameId).ToArray());
        Assert.Equal(["visual-015"], snapshot.VisualTreeSnapshots.Select(item => item.SnapshotId).ToArray());
        Assert.Equal(["artifact-015"], snapshot.ArtifactSnapshots.Select(item => item.SnapshotId).ToArray());
        Assert.Equal(["app-015"], snapshot.ApplicationEvents.Select(appEvent => appEvent.EventId).ToArray());
        Assert.Empty(snapshot.Analyses);

        var annotation = Assert.Single(snapshot.Annotations);
        Assert.Equal("annotation-spanning", annotation.AnnotationId);
        Assert.Equal(At(10), annotation.StartUtc);
        Assert.Equal(At(20), annotation.EndUtc);
        Assert.Equal("geometry-015", Assert.Single(annotation.Geometry).GeometryId);

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(importedSession.SessionId, out var persistedSnapshot)
                  && persistedSnapshot?.CreatedUtc == At(10)
                  && persistedSnapshot.LastUpdatedUtc == At(20),
            because: "Trimmed capture bounds should be persisted.");
        Assert.True(captureStore.TryLoad(importedSession.SessionId, out var persisted));
        Assert.NotNull(persisted);
        Assert.Equal(At(10), persisted!.CreatedUtc);
        Assert.Equal(At(20), persisted.LastUpdatedUtc);
        Assert.Null(persisted.AppStateChangedUtc);
        Assert.Equal(["frame-015"], persisted!.Images.Select(frame => frame.FrameId).ToArray());
        Assert.False(File.Exists(removedEarlyImagePath));
        Assert.True(File.Exists(keptImagePath));
        Assert.False(File.Exists(removedLateImagePath));
        Assert.True(Directory.Exists(keptArtifactDirectoryPath));
        Assert.False(Directory.Exists(removedArtifactDirectoryPath));
    }

    [Fact]
    public void ExtractSessionTimelineRange_CreatesNewSessionAndKeepsSourceUnchanged()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var frames = new[]
        {
            CreateImageFrame("frame-005", At(5)),
            CreateImageFrame("frame-015", At(15)),
            CreateImageFrame("frame-025", At(25))
        };

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-extract-001",
                AppId = "com.example.extract",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(40),
                IsHistorical = true,
                Images = frames,
                VisualTreeSnapshots =
                [
                    CreateVisualTreeSnapshot("visual-015", At(15)),
                    CreateVisualTreeSnapshot("visual-025", At(25))
                ],
                ArtifactSnapshots =
                [
                    CreateArtifactSnapshot("artifact-015", At(15)),
                    CreateArtifactSnapshot("artifact-025", At(25))
                ],
                Analyses =
                [
                    new SessionAnalysisRecord
                    {
                        AnalysisId = "analysis-001",
                        AgentId = "codex",
                        StartedUtc = At(30),
                        CompletedUtc = At(35),
                        Success = true
                    }
                ],
                Annotations =
                [
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-spanning",
                        StartUtc = At(5),
                        EndUtc = At(25),
                        Label = "Spanning annotation",
                        Geometry =
                        [
                            CreateAnnotationGeometry("geometry-015", "frame-015", At(15)),
                            CreateAnnotationGeometry("geometry-025", "frame-025", At(25))
                        ]
                    }
                ],
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                Touches =
                [
                    CreateTouch("touch-005", At(5)),
                    CreateTouch("touch-015", At(15)),
                    CreateTouch("touch-025", At(25))
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            frames.ToDictionary(frame => frame.FrameId, _ => new byte[] { 1, 2, 3 }),
            artifactBytesByRelativePath: new Dictionary<string, byte[]>
            {
                ["artifact-015/file.txt"] = [4, 5, 6],
                ["artifact-025/file.txt"] = [7, 8, 9]
            });

        Assert.True(importResult.IsSuccess);
        var sourceSession = importResult.ImportedSession!;

        var result = runtimeState.ExtractSessionTimelineRange(
            sourceSession.SessionId,
            At(10),
            At(20),
            "Checkout issue slice");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ExtractedSession);
        var extracted = result.ExtractedSession!;
        Assert.NotEqual(sourceSession.SessionId, extracted.SessionId);
        Assert.Equal("Checkout issue slice", extracted.Name);
        Assert.Equal(At(10), extracted.CreatedUtc);
        Assert.Equal(At(20), extracted.LastUpdatedUtc);
        Assert.Equal(["evt-015"], extracted.Logs.Select(log => log.EventId!).ToArray());
        Assert.Equal(["touch-015"], extracted.Touches.Select(touch => touch.Id).ToArray());
        Assert.Equal([15], extracted.Metrics.Select(metric => metric.Value).ToArray());
        Assert.Equal(["frame-015"], extracted.Images.Select(frame => frame.FrameId).ToArray());
        Assert.Equal(["visual-015"], extracted.VisualTreeSnapshots.Select(item => item.SnapshotId).ToArray());
        Assert.Equal(["artifact-015"], extracted.ArtifactSnapshots.Select(item => item.SnapshotId).ToArray());
        Assert.Empty(extracted.Analyses);

        var extractedAnnotation = Assert.Single(extracted.Annotations);
        Assert.Equal(At(10), extractedAnnotation.StartUtc);
        Assert.Equal(At(20), extractedAnnotation.EndUtc);
        Assert.Equal("geometry-015", Assert.Single(extractedAnnotation.Geometry).GeometryId);

        Assert.True(runtimeState.TryGetSessionSnapshot(sourceSession.SessionId, out var unchangedSource));
        Assert.NotNull(unchangedSource);
        Assert.Equal(["frame-005", "frame-015", "frame-025"], unchangedSource!.Images.Select(frame => frame.FrameId).ToArray());
        Assert.Single(unchangedSource.Analyses);

        Assert.True(captureStore.TryLoad(extracted.SessionId, out var persistedExtracted));
        Assert.NotNull(persistedExtracted);
        Assert.Equal(["frame-015"], persistedExtracted!.Images.Select(frame => frame.FrameId).ToArray());

        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var sourceKeptImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            sourceSession.AppId,
            sourceSession.SessionId,
            frames[1]);
        var extractedKeptImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            extracted.AppId,
            extracted.SessionId,
            frames[1]);
        var extractedRemovedImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            extracted.AppId,
            extracted.SessionId,
            frames[0]);
        var extractedDirectoryPath = SessionImageArtifactPath.ResolveSessionDirectoryPath(
            capturesRootPath,
            extracted.AppId,
            extracted.SessionId);
        Assert.True(File.Exists(sourceKeptImagePath));
        Assert.True(File.Exists(extractedKeptImagePath));
        Assert.False(File.Exists(extractedRemovedImagePath));
        Assert.True(Directory.Exists(Path.Combine(extractedDirectoryPath, "artifacts", "artifact-015")));
        Assert.False(Directory.Exists(Path.Combine(extractedDirectoryPath, "artifacts", "artifact-025")));
    }

    [Fact]
    public void ExtractSessionAnnotationBounds_UsesAnnotationRange()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-extract-annotation-001",
                AppId = "com.example.extract.annotation",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(40),
                IsHistorical = true,
                Annotations =
                [
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-bounds",
                        StartUtc = At(10),
                        EndUtc = At(20),
                        Label = "Checkout failure"
                    }
                ],
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            new Dictionary<string, byte[]>());

        Assert.True(importResult.IsSuccess);
        var sourceSession = importResult.ImportedSession!;

        var result = runtimeState.ExtractSessionAnnotationBounds(sourceSession.SessionId, "annotation-bounds");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.ExtractedSession);
        var extracted = result.ExtractedSession!;
        Assert.Equal(At(10), extracted.CreatedUtc);
        Assert.Equal(At(20), extracted.LastUpdatedUtc);
        Assert.Equal(["evt-015"], extracted.Logs.Select(log => log.EventId!).ToArray());
        Assert.Equal([15], extracted.Metrics.Select(metric => metric.Value).ToArray());
        Assert.Equal("Checkout failure", Assert.Single(extracted.Annotations).Label);
    }

    [Fact]
    public async Task UpdateSessionMetadata_AfterTimelineTrim_DoesNotExtendCaptureEnd()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-trim-metadata-001",
                AppId = "com.example.trim.metadata",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(40),
                IsHistorical = true,
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            new Dictionary<string, byte[]>());

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);

        var trimResult = runtimeState.TrimSessionTimeline(
            importResult.ImportedSession!.SessionId,
            At(10),
            At(20),
            SessionTimelineTrimMode.KeepSelectionOnly);
        Assert.True(trimResult.IsSuccess);

        var metadataResult = runtimeState.UpdateSessionMetadata(
            importResult.ImportedSession.SessionId,
            isPinned: true,
            tags: ["trimmed"],
            notes: "Reviewed after trim.",
            name: "Trimmed capture");
        Assert.True(metadataResult.IsSuccess);

        Assert.True(runtimeState.TryGetSessionSnapshot(importResult.ImportedSession.SessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(At(10), snapshot!.CreatedUtc);
        Assert.Equal(At(20), snapshot.LastUpdatedUtc);
        Assert.True(snapshot.IsPinned);
        Assert.Equal("Trimmed capture", snapshot.Name);

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(importResult.ImportedSession.SessionId, out var persistedSnapshot)
                  && persistedSnapshot?.CreatedUtc == At(10)
                  && persistedSnapshot.LastUpdatedUtc == At(20)
                  && persistedSnapshot.IsPinned,
            because: "Metadata persistence must not rewrite capture bounds.");
    }

    [Fact]
    public void TrimSessionTimeline_CutSelection_RemovesRangeAndRebuildsLogDeduplication()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        runtimeState.UpdateSessionMetricChannels(sessionId, [CreateMetricChannel()]);
        runtimeState.AddSessionMetrics(
            sessionId,
            [
                CreateMetric(5, At(5)),
                CreateMetric(15, At(15)),
                CreateMetric(25, At(25))
            ],
            telemetrySegmentId: 1);
        runtimeState.AddSessionLogs(
            sessionId,
            [
                CreateLog(At(5), "evt-005"),
                CreateLog(At(15), "evt-015"),
                CreateLog(At(25), "evt-025")
            ]);
        runtimeState.SetSessionStatus(sessionId, "WebSocket Closed");

        var result = runtimeState.TrimSessionTimeline(sessionId, At(10), At(20), SessionTimelineTrimMode.CutSelection);

        Assert.True(result.IsSuccess);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var trimmed));
        Assert.NotNull(trimmed);
        Assert.Equal(["evt-005", "evt-025"], trimmed!.Logs.Select(log => log.EventId!).ToArray());
        Assert.Equal([5, 25], trimmed.Metrics.Select(metric => metric.Value).ToArray());

        runtimeState.AddSessionLogs(sessionId, [CreateLog(At(30), "evt-015")]);

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var updated));
        Assert.NotNull(updated);
        Assert.Equal(["evt-005", "evt-025", "evt-015"], updated!.Logs.Select(log => log.EventId!).ToArray());
    }

    [Fact]
    public async Task TrimSessionTimeline_CutSelection_CompactsTimelineAndUpdatesRecordingBounds()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var frames = new[]
        {
            CreateImageFrame("frame-005", At(5)),
            CreateImageFrame("frame-015", At(15)),
            CreateImageFrame("frame-025", At(25))
        };

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-cut-001",
                AppId = "com.example.cut",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(30),
                IsHistorical = true,
                AppState = AppLifecycleState.Background,
                AppStateChangedUtc = At(25),
                Images = frames,
                VisualTreeSnapshots =
                [
                    new SessionVisualTreeSnapshot
                    {
                        SnapshotId = "visual-025",
                        CapturedAtUtc = At(25),
                        Source = "Replay",
                        NodeCount = 1,
                        ScreenshotFrameId = "frame-025",
                        ScreenshotCapturedAtUtc = At(25),
                        Payload = new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "root"
                        }
                    }
                ],
                ArtifactSnapshots =
                [
                    CreateArtifactSnapshot("artifact-015", At(15)),
                    CreateArtifactSnapshot("artifact-025", At(25))
                ],
                ApplicationEvents =
                [
                    new SessionApplicationEvent("app-005", "Before", "Debug", string.Empty, At(5), 255),
                    new SessionApplicationEvent("app-015", "Removed", "Debug", string.Empty, At(15), 255),
                    new SessionApplicationEvent("app-025", "After", "Debug", string.Empty, At(25), 255)
                ],
                Annotations =
                [
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-overlap",
                        StartUtc = At(12),
                        EndUtc = At(18),
                        Label = "Overlap"
                    },
                    new SessionAnnotation
                    {
                        AnnotationId = "annotation-after",
                        StartUtc = At(22),
                        EndUtc = At(28),
                        Label = "After",
                        Geometry =
                        [
                            CreateAnnotationGeometry("geometry-025", "frame-025", At(25))
                        ]
                    }
                ],
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                Touches =
                [
                    CreateTouch("touch-005", At(5)),
                    CreateTouch("touch-015", At(15)),
                    CreateTouch("touch-025", At(25))
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            frames.ToDictionary(frame => frame.FrameId, _ => new byte[] { 1, 2, 3 }),
            artifactBytesByRelativePath: new Dictionary<string, byte[]>
            {
                ["artifact-015/file.txt"] = [4, 5, 6],
                ["artifact-025/file.txt"] = [7, 8, 9]
            });

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);
        var importedSession = importResult.ImportedSession!;
        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var sessionDirectoryPath = SessionImageArtifactPath.ResolveSessionDirectoryPath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId);
        var removedImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId,
            frames[1]);
        var keptImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            capturesRootPath,
            importedSession.AppId,
            importedSession.SessionId,
            frames[2]);
        var removedArtifactDirectoryPath = Path.Combine(sessionDirectoryPath, "artifacts", "artifact-015");
        var keptArtifactDirectoryPath = Path.Combine(sessionDirectoryPath, "artifacts", "artifact-025");
        Assert.True(File.Exists(removedImagePath));
        Assert.True(File.Exists(keptImagePath));
        Assert.True(Directory.Exists(removedArtifactDirectoryPath));
        Assert.True(Directory.Exists(keptArtifactDirectoryPath));

        var result = runtimeState.TrimSessionTimeline(
            importedSession.SessionId,
            At(10),
            At(20),
            SessionTimelineTrimMode.CutSelection);

        Assert.True(result.IsSuccess);
        Assert.True(runtimeState.TryGetSessionSnapshot(importedSession.SessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(startedAtUtc, snapshot!.CreatedUtc);
        Assert.Equal(At(20), snapshot.LastUpdatedUtc);
        Assert.Equal(At(15), snapshot.AppStateChangedUtc);
        Assert.Equal([At(5), At(15)], snapshot.Logs.Select(log => log.TimestampUtc).ToArray());
        Assert.Equal(["touch-005", "touch-025"], snapshot.Touches.Select(touch => touch.Id).ToArray());
        Assert.Equal([At(5), At(15)], snapshot.Touches.Select(touch => touch.CapturedAtUtc).ToArray());
        Assert.Equal([5, 25], snapshot.Metrics.Select(metric => metric.Value).ToArray());
        Assert.Equal([At(5), At(15)], snapshot.Metrics.Select(metric => metric.CapturedAtUtc).ToArray());
        Assert.Equal(["frame-005", "frame-025"], snapshot.Images.Select(frame => frame.FrameId).ToArray());
        Assert.Equal([At(5), At(15)], snapshot.Images.Select(frame => frame.CapturedAtUtc).ToArray());

        var shiftedVisualTreeSnapshot = Assert.Single(snapshot.VisualTreeSnapshots);
        Assert.Equal(At(15), shiftedVisualTreeSnapshot.CapturedAtUtc);
        Assert.Equal(At(15), shiftedVisualTreeSnapshot.ScreenshotCapturedAtUtc);
        Assert.Equal(At(15), Assert.Single(snapshot.ArtifactSnapshots).CapturedAtUtc);
        Assert.Equal(["app-005", "app-025"], snapshot.ApplicationEvents.Select(appEvent => appEvent.EventId).ToArray());
        Assert.Equal([At(5), At(15)], snapshot.ApplicationEvents.Select(appEvent => appEvent.CapturedAtUtc).ToArray());

        var shiftedAnnotation = Assert.Single(snapshot.Annotations);
        Assert.Equal("annotation-after", shiftedAnnotation.AnnotationId);
        Assert.Equal(At(12), shiftedAnnotation.StartUtc);
        Assert.Equal(At(18), shiftedAnnotation.EndUtc);
        Assert.Equal(At(15), Assert.Single(shiftedAnnotation.Geometry).CapturedAtUtc);

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(importedSession.SessionId, out var persistedSnapshot)
                  && persistedSnapshot?.LastUpdatedUtc == At(20),
            because: "Compacted capture bounds should be persisted.");
        Assert.True(captureStore.TryLoad(importedSession.SessionId, out var persisted));
        Assert.NotNull(persisted);
        Assert.Equal(At(20), persisted!.LastUpdatedUtc);
        Assert.Equal([At(5), At(15)], persisted.Touches.Select(touch => touch.CapturedAtUtc).ToArray());
        Assert.Equal([At(5), At(15)], persisted.Metrics.Select(metric => metric.CapturedAtUtc).ToArray());
        Assert.False(File.Exists(removedImagePath));
        Assert.True(File.Exists(keptImagePath));
        Assert.False(Directory.Exists(removedArtifactDirectoryPath));
        Assert.True(Directory.Exists(keptArtifactDirectoryPath));
    }

    [Fact]
    public void ImportSessionSnapshot_WhenDeclaredEndLooksLikePersistenceTimestamp_UsesCapturedContentEnd()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);

        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = "import-stretched-001",
                AppId = "com.example.stretched",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(2 * 60),
                IsHistorical = true,
                Logs =
                [
                    CreateLog(At(12), "evt-012")
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(30, At(30))
                ]
            },
            new Dictionary<string, byte[]>());

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal(At(30), importResult.ImportedSession!.LastUpdatedUtc);

        Assert.True(runtimeState.TryGetSessionSnapshot(importResult.ImportedSession.SessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(At(30), snapshot!.LastUpdatedUtc);
        Assert.True(captureStore.TryLoad(importResult.ImportedSession.SessionId, out var persistedSnapshot));
        Assert.NotNull(persistedSnapshot);
        Assert.Equal(At(30), persistedSnapshot!.LastUpdatedUtc);
    }

    [Fact]
    public void ImportSessionSnapshot_WhenSessionIdAlreadyExists_AssignsANewSessionIdAndPersistsArtifacts()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);

        var existingSessionId = runtimeState.CreateSession("com.example.live", "Live Client", IPAddress.Loopback, null, null);
        var importedFrame = new SessionImageFrame
        {
            FrameId = "frame-001",
            CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:05Z"),
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 5
        };
        var importResult = runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = existingSessionId,
                AppId = "com.example.imported",
                ClientName = "Imported Client",
                RemoteAddress = "192.168.0.10",
                CreatedUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z"),
                ConfigId = "config-imported",
                ProcessSessionId = "proc-imported",
                Status = "WebSocket Closed",
                LastUpdatedUtc = DateTimeOffset.Parse("2026-04-01T05:01:00Z"),
                IsHistorical = false,
                IsPinned = true,
                Tags = ["imported", "regression"],
                Notes = "Imported from archive.",
                AppState = AppLifecycleState.Background,
                AppStateChangedUtc = DateTimeOffset.Parse("2026-04-01T05:00:30Z"),
                Images = [importedFrame],
                Logs =
                [
                    new LogEntry(DateTimeOffset.Parse("2026-04-01T05:00:10Z"), "Imported log")
                    {
                        Source = "Client",
                        EventId = "evt-imported"
                    }
                ],
                MetricChannels =
                [
                    new SessionMetricChannel
                    {
                        ChannelId = 1,
                        Name = "FPS",
                        ColorHex = "#00FF00"
                    }
                ],
                Metrics =
                [
                    new SessionMetricSample
                    {
                        ChannelId = 1,
                        Value = 55,
                        CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:20Z"),
                        SegmentId = 4
                    }
                ]
            },
            new Dictionary<string, byte[]>
            {
                [importedFrame.FrameId] = [1, 2, 3, 4, 5]
            });

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);
        Assert.NotEqual(existingSessionId, importResult.ImportedSession!.SessionId);
        Assert.Equal("com.example.imported", importResult.ImportedSession.AppId);
        Assert.True(importResult.ImportedSession.IsHistorical);
        Assert.Equal("proc-imported", importResult.ImportedSession.ProcessSessionId);
        Assert.Single(importResult.ImportedSession.Images);
        Assert.Single(importResult.ImportedSession.Logs);
        Assert.Single(importResult.ImportedSession.Metrics);

        Assert.True(captureStore.TryLoad(importResult.ImportedSession.SessionId, out var persistedSnapshot));
        Assert.NotNull(persistedSnapshot);
        Assert.Equal(importResult.ImportedSession.SessionId, persistedSnapshot!.SessionId);
        Assert.Equal("com.example.imported", persistedSnapshot.AppId);
        Assert.True(persistedSnapshot.IsPinned);
        Assert.Equal(["imported", "regression"], persistedSnapshot.Tags);
        Assert.Single(persistedSnapshot.Images);
        Assert.Single(persistedSnapshot.Logs);
        Assert.Single(persistedSnapshot.Metrics);
    }
}
