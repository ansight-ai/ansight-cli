using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void SaveAndTryLoad_RoundTripsFullSessionSnapshotAndDeleteRemovesIt()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var updatedUtc = createdUtc.AddMinutes(5);
        var imageFrame = new SessionImageFrame
        {
            FrameId = "frame-001",
            CapturedAtUtc = updatedUtc,
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 5
        };
        var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var sourceImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            imageFrame);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceImagePath)!);
        File.WriteAllBytes(sourceImagePath, [1, 2, 3, 4, 5]);
        var appIcon = new SessionAppIcon
        {
            FileName = "app-icon.png",
            Format = "png",
            MimeType = "image/png",
            Width = 2,
            Height = 2,
            ByteCount = 3
        };
        var appIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            appIcon);
        File.WriteAllBytes(appIconPath, [9, 8, 7]);

        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            ProcessSessionId = "proc-001",
            Status = "WebSocket Closed",
            LastUpdatedUtc = updatedUtc,
            IsHistorical = false,
            IsPinned = true,
            Tags = ["smoke-test", "ios"],
            Notes = "Session reproduced the onboarding crash.",
            CustomProperties = new JsonObject
            {
                ["app"] = new JsonObject
                {
                    ["tenant"] = "acme"
                },
                ["flags"] = new JsonObject
                {
                    ["beta"] = true
                }
            },
            AppState = AppLifecycleState.Background,
            AppStateChangedUtc = updatedUtc.AddSeconds(-30),
            DeviceProfile = new DeviceAppProfile
            {
                Sdk = new DeviceSdkProfile
                {
                    Name = "Ansight .NET SDK",
                    Version = "0.1.0-store",
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
                    AppId = "com.example.testapp",
                    AppName = "Example Test App",
                    VersionName = "1.0.0"
                }
            },
            AppIcon = appIcon,
            AppToolCatalog = new SessionAppToolCatalogSnapshot(
                "ansight.session-app-tool-catalog/v1",
                updatedUtc,
                new JsonObject
                {
                    ["schema"] = "ansight.tool-catalog.v3",
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "map.query_state",
                        ["policy"] = "read"
                    })
                },
                new JsonObject
                {
                    ["result"] = new JsonObject
                    {
                        ["providers"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "map.exports",
                            ["name"] = "Map exports"
                        })
                    }
                }),
            Analyses =
            [
                new SessionAnalysisRecord
                {
                    AnalysisId = "analysis-001",
                    AgentId = "agent-1",
                    StartedUtc = createdUtc,
                    CompletedUtc = updatedUtc,
                    Success = true,
                    FinalResponse = "Looks good."
                }
            ],
            Images =
            [
                imageFrame
            ],
            Annotations =
            [
                new SessionAnnotation
                {
                    AnnotationId = "annotation-001",
                    StartUtc = createdUtc.AddSeconds(20),
                    EndUtc = createdUtc.AddSeconds(35),
                    Label = "Launch crash",
                    Notes = "App crashed after the onboarding CTA was tapped.",
                    Status = "resolved",
                    Source = "sdk.annotatedFeedback",
                    CaptureGroupId = "capture-group-001",
                    CustomData = new JsonObject { ["flow"] = "onboarding" },
                    Evidence =
                    [
                        new SessionAnnotationEvidence
                        {
                            Id = "screenshot",
                            Kind = "screenshot",
                            Status = "captured",
                            CapturedAtUtc = imageFrame.CapturedAtUtc,
                            SizeBytes = imageFrame.ByteCount
                        }
                    ],
                    HookFailures = ["ExampleHook: unavailable"],
                    Geometry =
                    [
                        new SessionAnnotationGeometry
                        {
                            GeometryId = "geometry-001",
                            FrameId = imageFrame.FrameId,
                            CapturedAtUtc = imageFrame.CapturedAtUtc,
                            Kind = SessionAnnotationGeometryKind.Rectangle,
                            X = 0.15,
                            Y = 0.2,
                            Width = 0.3,
                            Height = 0.18,
                            StrokeColor = "#FFFF3B30",
                            StrokeWidth = 3
                        }
                    ]
                }
            ],
            AgentTaskLinks =
            [
                new SessionAgentTaskLink
                {
                    LinkId = "task-link-001",
                    SessionId = "session-001",
                    AnnotationBatchId = "capture-group-001",
                    AnnotationIds = ["annotation-001"],
                    FrameIds = ["frame-001"],
                    Source = "legacy-desktop",
                    Provider = "codex",
                    ProviderTaskId = "thread-001",
                    ProviderTurnId = "turn-001",
                    ProviderTaskTitle = "Fix onboarding crash",
                    WorkingDirectory = "/workspace",
                    SubmittedPrompt = "Fix the annotated onboarding crash.",
                    Status = "inProgress",
                    StatusMessage = "Accepted.",
                    CreatedAtUtc = updatedUtc,
                    UpdatedAtUtc = updatedUtc
                }
            ],
            ApplicationEvents =
            [
                new SessionApplicationEvent(
                    "event-login-started",
                    "auth.login.started",
                    "flow",
                    "Login request submitted",
                    createdUtc.AddSeconds(10),
                    4)
            ],
            VisualTreeSnapshots =
            [
                new SessionVisualTreeSnapshot
                {
                    SnapshotId = "visual-tree-001",
                    CapturedAtUtc = createdUtc.AddSeconds(40),
                    VisualTreeKind = "maui",
                    VisualTreeFormat = "ansight.maui.visual-tree.compact.v2",
                    RuntimePlatform = "ios",
                    Source = "legacy-desktop.dashboard.liveVisualTree",
                    RootScope = "currentPage",
                    MaxDepth = 12,
                    IncludeProperties = true,
                    IncludeBindableProperties = true,
                    NodeCount = 1,
                    Truncated = false,
                    ScreenshotFrameId = imageFrame.FrameId,
                    ScreenshotCapturedAtUtc = imageFrame.CapturedAtUtc,
                    ActionId = "ui-action-001",
                    ActionCapability = "ui.tap",
                    EvidencePhase = "before",
                    TreeHash = "tree-sha256",
                    ScreenshotHash = "screenshot-sha256",
                    Payload = new JsonObject
                    {
                        ["platform"] = "ios",
                        ["rootScope"] = "currentPage",
                        ["root"] = new JsonObject
                        {
                            ["id"] = "node-continue-button",
                            ["type"] = "Button",
                            ["kind"] = "View",
                            ["label"] = "Continue"
                        }
                    }
                }
            ],
            Logs =
            [
                new LogEntry(createdUtc, "Hello from host")
                {
                    Source = "Host",
                    Tag = "Test"
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
                    Value = 60,
                    CapturedAtUtc = createdUtc.AddSeconds(1)
                },
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = 58,
                    CapturedAtUtc = createdUtc.AddSeconds(2)
                }
            ]
        };

        store.Save(snapshot);
        var imagesDocumentPath = Path.Combine(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            "images.json");
        Assert.DoesNotContain("\"filePath\"", File.ReadAllText(imagesDocumentPath), StringComparison.OrdinalIgnoreCase);
        var visualTreesDirectoryPath = Path.Combine(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            "visual-trees");
        var visualTreeFilePath = Path.Combine(visualTreesDirectoryPath, "2026-03-20T00-00-40.0000000Z.json");
        Assert.True(File.Exists(visualTreeFilePath));
        Assert.False(File.Exists(Path.Combine(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            "visual-trees.json")));
        using (var visualTreeDocument = JsonDocument.Parse(File.ReadAllText(visualTreeFilePath)))
        {
            var root = visualTreeDocument.RootElement;
            Assert.Equal("ansight.session-visual-tree-snapshot.v2", root.GetProperty("schema").GetString());
            var header = root.GetProperty("header");
            Assert.Equal("visual-tree-001", header.GetProperty("snapshotId").GetString());
            Assert.Equal("maui", header.GetProperty("visualTreeKind").GetString());
            Assert.Equal("ansight.maui.visual-tree.compact.v2", header.GetProperty("visualTreeFormat").GetString());
            Assert.Equal("ios", header.GetProperty("runtimePlatform").GetString());
            Assert.Equal("visual-tree-001", root.GetProperty("snapshot").GetProperty("snapshotId").GetString());
        }

        var summary = Assert.Single(store.LoadSummaries());
        Assert.Equal(snapshot.SessionId, summary.SessionId);
        Assert.True(summary.IsHistorical);
        Assert.True(summary.IsPinned);
        Assert.Equal(["smoke-test", "ios"], summary.Tags);
        Assert.Equal("Session reproduced the onboarding crash.", summary.Notes);
        Assert.Equal("acme", summary.CustomProperties?["app"]?["tenant"]?.GetValue<string>());
        Assert.Equal(AppLifecycleState.Background, summary.AppState);
        Assert.Equal("0.1.0-store", summary.SdkVersion);
        Assert.Single(summary.Images);
        Assert.NotNull(summary.AppIcon);
        Assert.Single(summary.Annotations);
        var summaryTaskLink = Assert.Single(summary.AgentTaskLinks);
        Assert.Equal("thread-001", summaryTaskLink.ProviderTaskId);
        Assert.Equal("Fix the annotated onboarding crash.", summaryTaskLink.SubmittedPrompt);
        Assert.Single(summary.Analyses);
        Assert.Empty(summary.Logs);
        Assert.Empty(summary.Metrics);
        Assert.Equal(1, summary.TotalLogCount);
        Assert.Equal(1, summary.TotalAnnotationCount);
        Assert.Equal(1, summary.TotalImageCount);
        Assert.Equal(1, summary.TotalMetricChannelCount);
        Assert.Equal(2, summary.TotalMetricSampleCount);
        Assert.Equal(1, summary.TotalApplicationEventCount);
        Assert.True(summary.CacheSizeBytes > 0);
        Assert.True(store.TryGetSessionCacheSizeBytes(snapshot.SessionId, out var cacheSizeBytes));
        Assert.Equal(summary.CacheSizeBytes, cacheSizeBytes);

        var loadedSuccessfully = store.TryLoad(snapshot.SessionId, out var loadedSnapshot);
        Assert.True(loadedSuccessfully);
        var loaded = loadedSnapshot;

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Status, loaded!.Status);
        Assert.Equal("proc-001", loaded.ProcessSessionId);
        Assert.True(loaded.IsPinned);
        Assert.Equal(["smoke-test", "ios"], loaded.Tags);
        Assert.Equal("Session reproduced the onboarding crash.", loaded.Notes);
        Assert.Equal("acme", loaded.CustomProperties?["app"]?["tenant"]?.GetValue<string>());
        Assert.True(loaded.CustomProperties?["flags"]?["beta"]?.GetValue<bool>());
        Assert.Equal(snapshot.AppState, loaded.AppState);
        Assert.Equal(snapshot.AppStateChangedUtc, loaded.AppStateChangedUtc);
        Assert.Equal(summary.CacheSizeBytes, loaded.CacheSizeBytes);
        Assert.Equal("0.1.0-store", loaded.SdkVersion);
        Assert.Equal("Example Test App", loaded.DeviceProfile?.App?.AppName);
        Assert.NotNull(loaded.AppIcon);
        Assert.Equal("app-icon.png", loaded.AppIcon!.FileName);
        Assert.Equal("map.query_state", loaded.AppToolCatalog?.ToolCatalog["tools"]?[0]?["id"]?.GetValue<string>());
        Assert.Equal("map.exports", loaded.AppToolCatalog?.ArtifactCatalog?["result"]?["providers"]?[0]?["id"]?.GetValue<string>());
        var loadedAnnotation = Assert.Single(loaded.Annotations);
        Assert.Equal("Launch crash", loadedAnnotation.Label);
        Assert.Equal("App crashed after the onboarding CTA was tapped.", loadedAnnotation.Notes);
        Assert.Equal("resolved", loadedAnnotation.Status);
        Assert.Equal("capture-group-001", loadedAnnotation.CaptureGroupId);
        Assert.Equal("onboarding", loadedAnnotation.CustomData?["flow"]?.GetValue<string>());
        Assert.Single(loadedAnnotation.Evidence);
        Assert.Equal(["ExampleHook: unavailable"], loadedAnnotation.HookFailures);
        var loadedGeometry = Assert.Single(loadedAnnotation.Geometry);
        Assert.Equal(imageFrame.FrameId, loadedGeometry.FrameId);
        Assert.Equal(SessionAnnotationGeometryKind.Rectangle, loadedGeometry.Kind);
        Assert.Equal("#FFFF3B30", loadedGeometry.StrokeColor);
        Assert.Equal(3d, loadedGeometry.StrokeWidth);
        var loadedTaskLink = Assert.Single(loaded.AgentTaskLinks);
        Assert.Equal("task-link-001", loadedTaskLink.LinkId);
        Assert.Equal("turn-001", loadedTaskLink.ProviderTurnId);
        Assert.Equal(["annotation-001"], loadedTaskLink.AnnotationIds);
        Assert.Equal(["frame-001"], loadedTaskLink.FrameIds);
        Assert.Single(loaded.Logs);
        Assert.Single(loaded.MetricChannels);
        Assert.Equal(2, loaded.Metrics.Count);
        Assert.Equal(1, loaded.TotalLogCount);
        Assert.Equal(1, loaded.TotalAnnotationCount);
        Assert.Equal(1, loaded.TotalImageCount);
        Assert.Equal(1, loaded.TotalMetricChannelCount);
        Assert.Equal(2, loaded.TotalMetricSampleCount);
        var loadedApplicationEvent = Assert.Single(loaded.ApplicationEvents);
        Assert.Equal("auth.login.started", loadedApplicationEvent.Label);
        Assert.Equal((byte)4, loadedApplicationEvent.ChannelId);
        Assert.Equal(1, loaded.TotalApplicationEventCount);
        Assert.Single(loaded.Images);
        Assert.Single(loaded.Analyses);
        var loadedVisualTree = Assert.Single(loaded.VisualTreeSnapshots);
        Assert.Equal("visual-tree-001", loadedVisualTree.SnapshotId);
        Assert.Equal("maui", loadedVisualTree.VisualTreeKind);
        Assert.Equal("ansight.maui.visual-tree.compact.v2", loadedVisualTree.VisualTreeFormat);
        Assert.Equal("ios", loadedVisualTree.RuntimePlatform);
        Assert.Equal("ui-action-001", loadedVisualTree.ActionId);
        Assert.Equal("ui.tap", loadedVisualTree.ActionCapability);
        Assert.Equal("before", loadedVisualTree.EvidencePhase);
        Assert.Equal("tree-sha256", loadedVisualTree.TreeHash);
        Assert.Equal("screenshot-sha256", loadedVisualTree.ScreenshotHash);
        Assert.Equal("node-continue-button", loadedVisualTree.Payload["root"]?["id"]?.GetValue<string>());
        Assert.Equal(1, store.GetHighestSessionNumber());
        Assert.True(store.TryFindSessionId("com.example.testapp", "proc-001", out var foundSessionId));
        Assert.Equal(snapshot.SessionId, foundSessionId);

        Assert.True(store.Delete(snapshot.SessionId));
        Assert.False(store.TryLoad(snapshot.SessionId, out _));
        Assert.Empty(store.LoadSummaries());
    }
}
