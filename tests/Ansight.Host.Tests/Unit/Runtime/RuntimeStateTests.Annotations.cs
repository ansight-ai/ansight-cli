using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
    [Theory]
    [InlineData("", "ansight")]
    [InlineData("legacy-desktop", "legacy-desktop")]
    [InlineData("custom-source", "custom-source")]
    public void UpsertSessionAnnotation_UsesCurrentDefaultAndPreservesExplicitSource(string source, string expectedSource)
    {
        using var environment = new TestSupport.TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);

        var result = runtimeState.UpsertSessionAnnotation(sessionId, new SessionAnnotation
        {
            AnnotationId = "annotation-001",
            StartUtc = DateTimeOffset.UtcNow,
            Label = "Review this control",
            Source = source
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal(expectedSource, Assert.Single(snapshot!.Annotations).Source);
        Assert.Equal("ansight", new SessionAnnotation
        {
            AnnotationId = "default-source", StartUtc = DateTimeOffset.UtcNow, Label = "Default"
        }.Source);
    }

    [Fact]
    public void UpsertSessionAnnotation_PreservesArrowGeometry()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var annotation = new SessionAnnotation
        {
            AnnotationId = "annotation-draft-001",
            StartUtc = DateTimeOffset.Parse("2026-04-01T05:00:10Z"),
            Label = "Point to the broken control.",
            Geometry =
            [
                new SessionAnnotationGeometry
                {
                    GeometryId = "geometry-draft-001",
                    FrameId = "frame-001",
                    CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:10Z"),
                    Kind = SessionAnnotationGeometryKind.Arrow,
                    X = 0.1,
                    Y = 0.2,
                    Points =
                    [
                        new SessionAnnotationGeometryPoint { X = 0.1, Y = 0.2 },
                        new SessionAnnotationGeometryPoint { X = 0.8, Y = 0.7 }
                    ],
                    StrokeColor = "#FFFF3B30",
                    StrokeWidth = 3d
                }
            ]
        };

        var saveResult = runtimeState.UpsertSessionAnnotation(sessionId, annotation);

        Assert.True(saveResult.IsSuccess, saveResult.Message);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        var savedAnnotation = Assert.Single(snapshot!.Annotations);
        Assert.Equal("Point to the broken control.", savedAnnotation.Label);
        var savedGeometry = Assert.Single(savedAnnotation.Geometry);
        Assert.Equal(SessionAnnotationGeometryKind.Arrow, savedGeometry.Kind);
        Assert.Equal("#FFFF3B30", savedGeometry.StrokeColor);
    }

    [Fact]
    public void UpsertSessionAnnotation_RejectsBlankLabel()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var annotation = new SessionAnnotation
        {
            AnnotationId = "annotation-draft-001",
            StartUtc = DateTimeOffset.Parse("2026-04-01T05:00:10Z"),
            Label = "   "
        };

        var saveResult = runtimeState.UpsertSessionAnnotation(sessionId, annotation);

        Assert.False(saveResult.IsSuccess);
        Assert.Equal("Annotation label is required.", saveResult.Message);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Empty(snapshot!.Annotations);
    }

    [Fact]
    public async Task UpsertSessionAgentTaskLink_PublishesAndPersistsCanonicalProviderIdentity()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var createdAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:30Z");
        var taskLink = new SessionAgentTaskLink
        {
            LinkId = "link-001",
            SessionId = sessionId,
            AnnotationBatchId = "batch-001",
            AnnotationIds = ["annotation-001"],
            FrameIds = ["frame-001"],
            Source = "legacy-desktop",
            Provider = "Codex",
            ProviderTaskId = "thread-001",
            ProviderTurnId = "turn-001",
            ProviderTaskTitle = "Fix the annotated screen",
            WorkingDirectory = "/workspace",
            SubmittedPrompt = "Fix the annotated screen without changing unrelated behavior.",
            Status = "inProgress",
            StatusMessage = "Accepted.",
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };

        var result = runtimeState.UpsertSessionAgentTaskLink(sessionId, taskLink);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        var savedLink = Assert.Single(snapshot!.AgentTaskLinks);
        Assert.Equal("codex", savedLink.Provider);
        Assert.Equal("thread-001", savedLink.ProviderTaskId);
        Assert.Equal("turn-001", savedLink.ProviderTurnId);
        Assert.Equal(["annotation-001"], savedLink.AnnotationIds);

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionId, out var persistedSnapshot)
                  && persistedSnapshot?.AgentTaskLinks.SingleOrDefault()?.ProviderTaskId == "thread-001",
            because: "The linked provider task should remain reviewable after the host reloads the session.");
    }

    [Fact]
    public async Task UpsertAndDeleteSessionAnnotation_PersistsGeometryAndPublishesSnapshots()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var snapshots = new ConcurrentQueue<AppSessionSnapshot>();
        runtimeState.SessionUpdated += (_, snapshot) => snapshots.Enqueue(snapshot);

        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId),
            because: "Initial session snapshot should be published.");

        while (snapshots.TryDequeue(out _))
        {
        }

        var annotation = new SessionAnnotation
        {
            AnnotationId = "annotation-001",
            StartUtc = DateTimeOffset.Parse("2026-04-01T05:00:10Z"),
            EndUtc = DateTimeOffset.Parse("2026-04-01T05:00:20Z"),
            Label = "Launch crash",
            Notes = "Crash reproduced after tapping Continue.",
            Geometry =
            [
                new SessionAnnotationGeometry
                {
                    GeometryId = "geometry-001",
                    FrameId = "frame-001",
                    CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:15Z"),
                    Kind = SessionAnnotationGeometryKind.Rectangle,
                    X = 0.12,
                    Y = 0.18,
                    Width = 0.34,
                    Height = 0.22
                }
            ]
        };

        var saveResult = runtimeState.UpsertSessionAnnotation(sessionId, annotation);
        Assert.True(saveResult.IsSuccess);

        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId && snapshot.Annotations.Count == 1),
            because: "Saving an annotation should publish an updated session snapshot.");

        var updated = Assert.Single(snapshots);
        var updatedAnnotation = Assert.Single(updated.Annotations);
        Assert.Equal("Launch crash", updatedAnnotation.Label);
        Assert.Equal("Crash reproduced after tapping Continue.", updatedAnnotation.Notes);
        Assert.Single(updatedAnnotation.Geometry);

        AppSessionSnapshot? persistedAfterSave = null;
        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionId, out persistedAfterSave)
                  && persistedAfterSave?.Annotations.Count == 1,
            because: "The annotation should be persisted on the slower capture-store cadence.");
        Assert.NotNull(persistedAfterSave);
        Assert.Single(persistedAfterSave!.Annotations);

        while (snapshots.TryDequeue(out _))
        {
        }

        var deleteResult = runtimeState.DeleteSessionAnnotation(sessionId, annotation.AnnotationId);
        Assert.True(deleteResult.IsSuccess);

        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId && snapshot.Annotations.Count == 0),
            because: "Deleting an annotation should publish an updated session snapshot.");

        var deleted = Assert.Single(snapshots);
        Assert.Empty(deleted.Annotations);
        AppSessionSnapshot? persistedAfterDelete = null;
        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionId, out persistedAfterDelete)
                  && persistedAfterDelete?.Annotations.Count == 0,
            because: "The annotation deletion should be persisted on the slower capture-store cadence.");
        Assert.NotNull(persistedAfterDelete);
        Assert.Empty(persistedAfterDelete!.Annotations);
    }
}
