using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AnnotationMutationTests
{
    [Fact]
    public async Task Create_RejectsDuplicateIdsAtomicallyAndUsesSessionTimestamp()
    {
        using var context = new AnnotationMutationTestContext();
        var initial = context.Snapshot;
        var create = ReadMutation(SessionAnnotationMutationKind.Create, """{"annotationId":"review","label":"Review"}""");
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => context.State.MutateSessionAnnotation(context.SessionId, create))));

        var result = Assert.Single(results, result => result.IsSuccess);
        Assert.Equal("review", result.Payload?["annotationId"]?.GetValue<string>());
        var annotation = Assert.Single(context.Snapshot.Annotations);
        Assert.Equal(initial.LastUpdatedUtc, annotation.StartUtc);
        Assert.Equal("ansight-operation", annotation.Source);
        Assert.All(results.Where(result => !result.IsSuccess), result => Assert.Contains("already exists", result.Message));
    }

    [Fact]
    public async Task Patch_ConcurrentDisjointFieldsPreserveEachOthersChanges()
    {
        using var context = new AnnotationMutationTestContext();
        Assert.True(context.Mutate(SessionAnnotationMutationKind.Create, """{"annotationId":"review","label":"Original","notes":"Before","status":"needs review"}""").IsSuccess);
        var label = ReadMutation(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","label":"Updated"}""");
        var notes = ReadMutation(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","notes":"After"}""");

        var results = await Task.WhenAll(
            Task.Run(() => context.State.MutateSessionAnnotation(context.SessionId, label)),
            Task.Run(() => context.State.MutateSessionAnnotation(context.SessionId, notes)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        var annotation = Assert.Single(context.Snapshot.Annotations);
        Assert.Equal("Updated", annotation.Label);
        Assert.Equal("After", annotation.Notes);
        Assert.Equal("needs review", annotation.Status);
    }

    [Fact]
    public void Patch_PreservesEvidenceAndSupportsExplicitClearing()
    {
        using var context = new AnnotationMutationTestContext();
        var start = DateTimeOffset.Parse("2026-09-08T02:00:00Z");
        Assert.True(context.State.UpsertSessionAnnotation(context.SessionId, new SessionAnnotation
        {
            AnnotationId = "review",
            Label = "Before",
            Source = "sdk",
            Notes = "Keep these notes",
            Status = "open",
            StartUtc = start,
            EndUtc = start.AddSeconds(10),
            CaptureGroupId = "capture-group",
            CustomData = new JsonObject { ["issue"] = 123 },
            Evidence = [new SessionAnnotationEvidence { Id = "evidence", Kind = "screenshot", Status = "captured" }],
            HookFailures = ["optional capture failed"],
            Geometry = [new SessionAnnotationGeometry
            {
                GeometryId = "shape",
                FrameId = "legacy-frame",
                CapturedAtUtc = start.AddSeconds(1),
                Kind = SessionAnnotationGeometryKind.Arrow,
                X = 0.1,
                Y = 0.2,
                Points = [new SessionAnnotationGeometryPoint { X = 0.1, Y = 0.2 }, new SessionAnnotationGeometryPoint { X = 0.8, Y = 0.7 }]
            }],
            Target = new SessionAnnotationTarget { TargetId = "legacy-node", VisualTreeSnapshotId = "legacy-tree" }
        }).IsSuccess);

        var updated = context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","expectedSource":"sdk","label":"After","startUtc":"2026-09-08T02:00:02Z"}""");
        Assert.True(updated.IsSuccess, updated.Message);
        var saved = Assert.Single(context.Snapshot.Annotations);
        Assert.Equal("After", saved.Label);
        Assert.Equal("sdk", saved.Source);
        Assert.Equal("Keep these notes", saved.Notes);
        Assert.Equal("open", saved.Status);
        Assert.Equal(start.AddSeconds(1), Assert.Single(saved.Geometry).CapturedAtUtc);
        Assert.Equal(SessionAnnotationGeometryKind.Arrow, saved.Geometry[0].Kind);
        Assert.Equal("capture-group", saved.CaptureGroupId);
        Assert.Equal(123, saved.CustomData?["issue"]?.GetValue<int>());
        Assert.Equal("evidence", Assert.Single(saved.Evidence).Id);
        Assert.Equal("optional capture failed", Assert.Single(saved.HookFailures));

        var resolved = context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","status":"resolved"}""");
        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.Equal("resolved", resolved.Payload?["annotation"]?["status"]?.GetValue<string>());

        var cleared = context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","notes":null,"status":null,"endUtc":null,"target":null,"geometries":[]}""");
        Assert.True(cleared.IsSuccess, cleared.Message);
        saved = Assert.Single(context.Snapshot.Annotations);
        Assert.Null(saved.Notes);
        Assert.Null(saved.Status);
        Assert.Null(saved.EndUtc);
        Assert.Null(saved.Target);
        Assert.Empty(saved.Geometry);
        Assert.Equal("capture-group", saved.CaptureGroupId);
        Assert.Single(saved.Evidence);
        Assert.Single(saved.HookFailures);
        Assert.True(context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review"}""").IsSuccess);
    }

    [Fact]
    public void PatchAndRemove_RejectMissingIdsSourceMismatchAndInvalidResultingRange()
    {
        using var context = new AnnotationMutationTestContext();
        Assert.True(context.Mutate(SessionAnnotationMutationKind.Create, """{"annotationId":"review","source":"task:checkout","label":"Before","startUtc":"2026-09-08T00:00:00Z","endUtc":"2026-09-08T00:00:01Z"}""").IsSuccess);

        Assert.False(context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"missing","label":"After"}""").IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Remove, """{"annotationId":"missing"}""").IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","expectedSource":"other","label":"After"}""").IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Remove, """{"annotationId":"review","expectedSource":"other"}""").IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Patch, """{"annotationId":"review","startUtc":"2026-09-08T00:00:02Z"}""").IsSuccess);
        Assert.Equal("Before", Assert.Single(context.Snapshot.Annotations).Label);

        var result = context.Mutate(SessionAnnotationMutationKind.Remove, """{"annotationId":"review","expectedSource":"task:checkout"}""");
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("review", result.Payload?["deletedAnnotation"]?["annotationId"]?.GetValue<string>());
        Assert.Empty(context.Snapshot.Annotations);
    }

    [Fact]
    public void PatchAndRemove_RequireAnExactSourceGuardIncludingWhitespace()
    {
        using var context = new AnnotationMutationTestContext();
        Assert.True(context.Mutate(SessionAnnotationMutationKind.Create,
            """{"annotationId":"review","source":"task:checkout","label":"Before"}""").IsSuccess);

        var patched = context.Mutate(SessionAnnotationMutationKind.Patch,
            """{"annotationId":"review","expectedSource":" task:checkout ","label":"After"}""");
        var removed = context.Mutate(SessionAnnotationMutationKind.Remove,
            """{"annotationId":"review","expectedSource":" task:checkout "}""");

        Assert.False(patched.IsSuccess);
        Assert.False(removed.IsSuccess);
        var unchanged = Assert.Single(context.Snapshot.Annotations);
        Assert.Equal("Before", unchanged.Label);
        Assert.Equal("task:checkout", unchanged.Source);
    }

    [Fact]
    public async Task Create_ResolvesFrameTimeTargetAndGeometryAndPersistsTheResult()
    {
        using var context = new AnnotationMutationTestContext();
        var capturedAt = DateTimeOffset.Parse("2026-09-08T03:00:00Z");
        var frame = await context.AddFrameAsync(capturedAt);
        context.AddTree("tree", "pay");
        var input = new JsonObject
        {
            ["label"] = " Checkout ",
            ["startUtc"] = "2026-09-08T03:00:05Z",
            ["endUtc"] = "2026-09-08T03:00:05Z",
            ["target"] = new JsonObject { ["visualTreeSnapshotId"] = "tree", ["nodeId"] = "pay" },
            ["geometries"] = new JsonArray(
                new JsonObject { ["frameId"] = frame.FrameId, ["kind"] = "point", ["x"] = 0, ["y"] = 1 },
                new JsonObject
                {
                    ["frameId"] = frame.FrameId,
                    ["kind"] = "freeDraw",
                    ["points"] = new JsonArray(new JsonObject { ["x"] = 0.2, ["y"] = 0.3 }, new JsonObject { ["x"] = 0.8, ["y"] = 0.5 })
                })
        };
        Assert.True(AnnotationMutationArgumentReader.TryRead(input, SessionAnnotationMutationKind.Create, out var mutation, out var error), error);
        var result = context.State.MutateSessionAnnotation(context.SessionId, mutation!);

        Assert.True(result.IsSuccess, result.Message);
        var saved = Assert.Single(context.Snapshot.Annotations);
        Assert.Equal("Checkout", saved.Label);
        Assert.Null(saved.EndUtc);
        Assert.All(saved.Geometry, geometry => Assert.Equal(capturedAt, geometry.CapturedAtUtc));
        var path = Assert.Single(saved.Geometry, geometry => geometry.Kind == SessionAnnotationGeometryKind.FreeDraw);
        Assert.Equal(0.2, path.X);
        Assert.Equal(0.6, path.Width!.Value, 12);
        Assert.Equal("pay", saved.Target!.TargetId);
        Assert.Equal("tree", saved.Target.VisualTreeSnapshotId);
        Assert.Equal("Button", saved.Target.Type);
        Assert.Equal("Pay now", saved.Target.Label);
        Assert.Equal("checkout.pay", saved.Target.AutomationId);
        Assert.Equal(1, saved.Target.Depth);
        Assert.Equal(100, saved.Target.AbsoluteBounds!.X);
        Assert.Equal(0.1, saved.Target.NormalizedBounds!.X);

        await TestWait.UntilAsync(
            () => context.Store.TryLoad(context.SessionId, out var persisted) && persisted?.Annotations.Count == 1,
            because: "The annotation and screenshot geometry should remain available after a session reload.");
        Assert.True(context.Store.TryLoad(context.SessionId, out var snapshot));
        Assert.Equal(capturedAt, Assert.Single(snapshot!.Annotations).Geometry[0].CapturedAtUtc);
    }

    [Fact]
    public async Task Create_RejectsForeignFramesTreesAndUnknownNodesWithoutMutation()
    {
        using var context = new AnnotationMutationTestContext();
        var foreignSession = context.State.CreateSession("other-app", "Other", IPAddress.Loopback, null, null);
        var foreignFrame = await context.State.AddSessionEvidenceImageAsync(foreignSession, DateTimeOffset.UtcNow, "png", 10, 10, 100, new byte[] { 1, 2, 3 });
        Assert.NotNull(foreignFrame);
        context.AddTree("foreign-tree", "foreign-node", foreignSession);
        context.AddTree("local-tree", "local-node");

        var geometry = new JsonObject
        {
            ["label"] = "Foreign screenshot",
            ["geometries"] = new JsonArray(new JsonObject { ["frameId"] = foreignFrame!.FrameId, ["kind"] = "point", ["x"] = 0.5, ["y"] = 0.5 })
        };
        Assert.True(AnnotationMutationArgumentReader.TryRead(geometry, SessionAnnotationMutationKind.Create, out var mutation, out _));
        Assert.False(context.State.MutateSessionAnnotation(context.SessionId, mutation!).IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Create, """{"label":"Wrong tree","target":{"visualTreeSnapshotId":"foreign-tree","nodeId":"foreign-node"}}""").IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Create, """{"label":"Missing node","target":{"visualTreeSnapshotId":"local-tree","nodeId":"foreign-node"}}""").IsSuccess);
        Assert.Empty(context.Snapshot.Annotations);
    }

    [Fact]
    public void Mutations_RejectOversizedResponsesBeforeChangingState()
    {
        using var context = new AnnotationMutationTestContext();
        var oversized = new string('x', 490_000);
        var createInput = new JsonObject { ["annotationId"] = "too-big", ["label"] = oversized };
        Assert.True(AnnotationMutationArgumentReader.TryRead(createInput, SessionAnnotationMutationKind.Create, out var create, out _));
        Assert.False(context.State.MutateSessionAnnotation(context.SessionId, create!).IsSuccess);
        Assert.Empty(context.Snapshot.Annotations);

        Assert.True(context.Mutate(SessionAnnotationMutationKind.Create, """{"annotationId":"review","label":"Before"}""").IsSuccess);
        var patchInput = new JsonObject { ["annotationId"] = "review", ["notes"] = oversized };
        Assert.True(AnnotationMutationArgumentReader.TryRead(patchInput, SessionAnnotationMutationKind.Patch, out var patch, out _));
        Assert.False(context.State.MutateSessionAnnotation(context.SessionId, patch!).IsSuccess);
        Assert.Null(Assert.Single(context.Snapshot.Annotations).Notes);

        Assert.True(context.State.UpsertSessionAnnotation(context.SessionId, new SessionAnnotation
        {
            AnnotationId = "legacy-big",
            StartUtc = DateTimeOffset.UtcNow,
            Label = "Legacy data",
            CustomData = new JsonObject { ["large"] = oversized }
        }).IsSuccess);
        Assert.False(context.Mutate(SessionAnnotationMutationKind.Remove, """{"annotationId":"legacy-big"}""").IsSuccess);
        Assert.Equal(2, context.Snapshot.Annotations.Count);
    }

    [Theory]
    [InlineData("{\"label\":\" \"}")]
    [InlineData("{\"label\":\"Review\",\"source\":null}")]
    [InlineData("{\"label\":\"Review\",\"startUtc\":\"2026-09-08\"}")]
    [InlineData("{\"label\":\"Review\",\"startUtc\":\"2026-09-08T12:00:00\"}")]
    [InlineData("{\"label\":\"Review\",\"notes\":null}")]
    [InlineData("{\"label\":\"Review\",\"captureGroupId\":\"new\"}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":null}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"point\",\"frameId\":\"f\",\"x\":1.1,\"y\":0}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"point\",\"frameId\":\"f\",\"x\":1e400,\"y\":0}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"point\",\"frameId\":\"f\",\"x\":0,\"y\":0,\"capturedAtUtc\":\"2026-09-08T00:00:00Z\"}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"rectangle\",\"frameId\":\"f\",\"x\":0.8,\"y\":0,\"width\":0.3,\"height\":0.1}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"rectangle\",\"frameId\":\"f\",\"x\":0,\"y\":0,\"width\":0,\"height\":0.1}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"freeDraw\",\"frameId\":\"f\",\"points\":[{\"x\":0,\"y\":0}]}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"freeDraw\",\"frameId\":\"f\",\"points\":[{\"x\":0,\"y\":0},{\"x\":0,\"y\":0}]}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"point\",\"frameId\":\"f\",\"x\":0,\"y\":0,\"strokeWidth\":0}]}")]
    [InlineData("{\"label\":\"Review\",\"geometries\":[{\"kind\":\"point\",\"frameId\":\"f\",\"x\":0,\"y\":0,\"strokeColor\":\"javascript:test\"}]}")]
    [InlineData("{\"label\":\"Review\",\"target\":{\"visualTreeSnapshotId\":\"t\",\"targetId\":\"n\"}}")]
    public void Parser_RejectsInvalidInputs(string json)
    {
        Assert.False(AnnotationMutationArgumentReader.TryRead(JsonNode.Parse(json)!.AsObject(), SessionAnnotationMutationKind.Create, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parser_PatchCannotChangeProvenance()
    {
        Assert.False(AnnotationMutationArgumentReader.TryRead(
            new JsonObject { ["annotationId"] = "review", ["source"] = "replacement" },
            SessionAnnotationMutationKind.Patch, out _, out _));
    }

    internal static SessionAnnotationMutation ReadMutation(SessionAnnotationMutationKind kind, string json)
    {
        Assert.True(AnnotationMutationArgumentReader.TryRead(JsonNode.Parse(json)!.AsObject(), kind, out var mutation, out var error), error);
        return mutation!;
    }
}
