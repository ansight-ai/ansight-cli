using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class AutomationEvidenceTaskTests
{
    [Fact]
    public async Task TaskBridge_InspectsNetworkAndEditsScreenshotAnnotationsWithPortableEvidenceRequirement()
    {
        using var directory = TestDirectory.Create();
        var fixture = new Fixture(directory.Path);
        var result = await fixture.RunAsync("""
            const requests = await ansight.network.get({ failedOnly: true, sessionId: "foreign" });
            expect(requests.requests.map(request => request.id), { id: "session-network" }).toEqual(["request-current"]);
            expect(requests.requests[0].requestHeaders, { id: "summary-no-headers" }).toBeUndefined();
            const detail = await ansight.network.getRequest({ requestId: "request-current" });
            expect(detail.request.responseHeaders.map(header => header.value), { id: "duplicate-headers" }).toEqual(["first", "second"]);
            const response = await ansight.network.readBody({ requestId: "request-current", side: "response", maxBytes: 4 });
            expect(response.body.data, { id: "bounded-body" }).toBe("fail");
            expect(response.body.isTruncated, { id: "body-read-truncated" }).toBe(true);
            const created = await ansight.annotations.create({
              sessionId: "foreign", label: "Checkout failed", notes: "original",
              startUtc: detail.request.startedAtUtc, endUtc: detail.request.completedAtUtc,
              geometries: [{ kind: "rectangle", frameId: "frame-current", x: 0.1, y: 0.2, width: 0.3, height: 0.4 }]
            });
            expect(created.sessionId, { id: "annotation-session" }).toBe(run.sessionId);
            expect(created.annotation.source, { id: "task-provenance" }).toBe(`task:${run.taskId}`);
            const found = await ansight.annotations.get({ frameId: "frame-current", hasGeometry: true });
            expect(found.annotations.map(annotation => annotation.annotationId), { id: "read-created" }).toEqual([created.annotationId]);
            const updated = await ansight.annotations.update({ annotationId: created.annotationId, notes: null, endUtc: null });
            expect(updated.annotation.notes, { id: "clear-notes" }).toBeNull();
            expect(updated.annotation.endUtc, { id: "clear-end" }).toBeNull();
            expect(updated.annotation.geometryCount, { id: "preserve-geometry" }).toBe(1);
            expect(updated.annotation.label, { id: "preserve-label" }).toBe("Checkout failed");
            const timeline = await ansight.session.getTimeline({ categories: ["networkRequest", "annotation"] });
            expect(timeline.events.map(event => event.category), { id: "unified-evidence" }).toEqual(["networkRequest", "annotation"]);
            expect(timeline.events[0].details.requestId, { id: "timeline-request-id" }).toBe("request-current");
            const removed = await ansight.annotations.delete({ annotationId: created.annotationId, expectedSource: created.annotation.source });
            expect(removed.deletedAnnotation.annotationId, { id: "deleted-record" }).toBe(created.annotationId);
            const remaining = await ansight.annotations.get();
            expect(remaining.matchedAnnotationCount, { id: "removed" }).toBe(0);
            return { annotationId: created.annotationId };
            """);

        Assert.True(result.Status == RepositoryTaskRunStatus.Passed, result.Message + result.StandardError);
        Assert.Equal(9, result.ToolCalls.Count);
        Assert.All(result.ToolCalls, call => Assert.False(call.IsError));
        var createCall = Assert.Single(result.ToolCalls, call => call.ToolName == "ansight_create_annotation");
        var createPayload = JsonNode.Parse(createCall.Result!.Content)!;
        Assert.Equal(result.Output!["annotationId"]!.GetValue<string>(), createPayload["annotationId"]!.GetValue<string>());
        Assert.Equal("task:evidence", createPayload["annotation"]!["source"]!.GetValue<string>());
        Assert.True(fixture.Store.TryLoad("foreign", out var foreign));
        Assert.Empty(foreign!.Annotations);
        await TestWait.UntilAsync(() => fixture.Store.TryLoad("current", out var persisted)
            && persisted!.Annotations.Count == 0,
            because: "The annotation deletion should reach the capture store before checking persisted evidence.");
        Assert.True(fixture.Store.TryLoad("current", out var current));
        Assert.Empty(current!.Annotations);
    }

    [Fact]
    public async Task TaskBridge_RejectsForeignEvidenceAndKeepsAnnotationWhenAssertionFails()
    {
        using var directory = TestDirectory.Create();
        var fixture = new Fixture(directory.Path);
        var result = await fixture.RunAsync("""
            let foreignRequestRejected = false;
            try { await ansight.network.getRequest({ sessionId: "foreign", requestId: "request-foreign" }); }
            catch { foreignRequestRejected = true; }
            expect(foreignRequestRejected, { id: "foreign-request-rejected" }).toBe(true);
            let foreignFrameRejected = false;
            try { await ansight.annotations.create({ label: "wrong frame", geometries: [{ kind: "point", frameId: "frame-foreign", x: 0.5, y: 0.5 }] }); }
            catch { foreignFrameRejected = true; }
            expect(foreignFrameRejected, { id: "foreign-frame-rejected" }).toBe(true);
            const created = await ansight.annotations.create({ annotationId: "retained-evidence", label: "Investigate failure", geometries: [{ kind: "point", frameId: "frame-current", x: 0.5, y: 0.5 }] });
            await ansight.annotations.update({ annotationId: created.annotationId, notes: "Response inspection failed" });
            expect(false, { id: "deliberate-failure" }).toBe(true);
            """);

        Assert.True(result.Status == RepositoryTaskRunStatus.Failed, result.Message + result.StandardError);
        Assert.Equal(4, result.ToolCalls.Count);
        Assert.True(result.ToolCalls[0].IsError);
        Assert.True(result.ToolCalls[1].IsError);
        await TestWait.UntilAsync(() => fixture.Store.TryLoad("current", out var persisted)
            && persisted!.Annotations.Any(annotation => annotation.AnnotationId == "retained-evidence"
                && annotation.Notes == "Response inspection failed"),
            because: "The host should persist annotation edits even when a later task assertion fails.");
        Assert.True(fixture.Store.TryLoad("current", out var saved));
        var annotation = Assert.Single(saved!.Annotations);
        Assert.Equal("retained-evidence", annotation.AnnotationId);
        Assert.Equal("Response inspection failed", annotation.Notes);
        Assert.Equal("frame-current", Assert.Single(annotation.Geometry).FrameId);
        var events = SessionTimelineBuilder.BuildEvents(saved, null, null);
        Assert.Contains(events, item => item.Payload["category"]!.GetValue<string>() == "annotation"
            && item.Payload["details"]!["annotationId"]!.GetValue<string>() == annotation.AnnotationId);
        var patchResult = JsonNode.Parse(result.ToolCalls[3].Result!.Content)!;
        Assert.Equal(annotation.Notes, patchResult["annotation"]!["notes"]!.GetValue<string>());
        Assert.True(fixture.Store.TryLoad("foreign", out var foreign));
        Assert.Empty(foreign!.Annotations);
    }

    [Fact]
    public void NetworkTimeline_UsesStartTimeForInclusiveFilteringAndDoesNotIncludeBodies()
    {
        using var directory = TestDirectory.Create();
        var fixture = new Fixture(directory.Path);
        Assert.True(fixture.Store.TryLoad("current", out var snapshot));
        var request = Assert.Single(snapshot!.NetworkRequests);
        var atStart = SessionTimelineBuilder.BuildEvents(snapshot, request.StartedAtUtc, request.StartedAtUtc);
        var captured = Assert.Single(atStart, item => item.Payload["category"]!.GetValue<string>() == "networkRequest");
        Assert.Equal(request.StartedAtUtc, captured.TimestampUtc);
        var details = captured.Payload["details"]!.AsObject();
        Assert.Equal(request.Id, details["requestId"]!.GetValue<string>());
        Assert.Equal(request.CompletedAtUtc, details["completedAtUtc"]!.GetValue<DateTimeOffset>());
        Assert.False(details.ContainsKey("responseBody"));
        Assert.DoesNotContain(SessionTimelineBuilder.BuildEvents(snapshot, request.CompletedAtUtc, null),
            item => item.Payload["category"]!.GetValue<string>() == "networkRequest");
    }

    private sealed class Fixture
    {
        private readonly string rootPath;
        private readonly RepositoryTaskRouter router;

        public Fixture(string rootPath)
        {
            this.rootPath = rootPath;
            var paths = new DataToolApplicationPaths(rootPath);
            Store = new SessionCaptureStore(paths);
            var state = new RuntimeState(Store);
            Seed(state, "current");
            Seed(state, "foreign");
            router = new RepositoryTaskRouter(rootPath);
            var bridge = new UnusedAppBridge();
            var catalog = new ToolCatalog(state, paths, new KnownAppStore(paths),
                new UnusedPairingService(), new EmptyPairingCache(), bridge, null,
                new SessionResolver(state, bridge), repositoryTaskRouter: router);
            router.ConfigureToolExecutor((name, arguments, correlationId) => catalog.HandleToolsCallAsync(
                new JsonObject { ["name"] = name, ["arguments"] = arguments }, correlationId));
        }

        public SessionCaptureStore Store { get; }

        public async Task<RepositoryTaskRunResult> RunAsync(string body)
        {
            var taskDirectory = Path.Combine(rootPath, "ansight", "tasks");
            Directory.CreateDirectory(taskDirectory);
            File.WriteAllText(Path.Combine(taskDirectory, "ansight-task.d.ts"), RepositoryModuleContractArtifacts.GetTaskTypeDefinitions());
            File.WriteAllText(Path.Combine(taskDirectory, "evidence.ts"), """
                import type { TaskInvocation } from "./ansight-task.d.ts";
                export const task = { "schemaVersion": 2, "title": "Inspect captured evidence", "description": "Inspect and annotate captured HTTP failure evidence.", "maximumActions": 20, "timeoutSeconds": 20, "requires": { "capabilities": ["evidence.read"] } };
                export default async function run({ ansight, expect, run }: TaskInvocation) {
                """ + body + "\n}");
            var loaded = router.Load(rootPath, "com.example.app");
            Assert.True(loaded.Warnings.Count == 0, string.Join(Environment.NewLine, loaded.Warnings));
            var task = Assert.Single(loaded.Tasks);
            Assert.Empty(task.DeclaredHostTools);
            using var trace = RepositoryTaskTraceScope.Begin(true);
            return await router.ExecuteAsync(task, "current", new JsonObject(), "evidence-test", CancellationToken.None);
        }

        private static void Seed(RuntimeState state, string sessionId)
        {
            var timestamp = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
            var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6fWQAAAAASUVORK5CYII=");
            var snapshot = new AppSessionSnapshot
            {
                SessionId = sessionId, AppId = "com.example.app", ClientName = "Evidence Test",
                RemoteAddress = "127.0.0.1", CreatedUtc = timestamp, ConfigId = null,
                Status = "WebSocket Closed", LastUpdatedUtc = timestamp.AddSeconds(1), IsHistorical = true,
                MetricChannels = [], Metrics = [],
                Images = [new SessionImageFrame
                {
                    FrameId = $"frame-{sessionId}", CapturedAtUtc = timestamp.AddMilliseconds(150),
                    Format = "png", Width = 1, Height = 1, Quality = 100, ByteCount = image.Length
                }],
                NetworkRequests = [new SessionNetworkRequest
                {
                    Id = $"request-{sessionId}", Source = "test", StartedAtUtc = timestamp,
                    CompletedAtUtc = timestamp.AddMilliseconds(100), DurationMilliseconds = 100,
                    Method = "POST", Url = "https://example.test/checkout", StatusCode = 503,
                    ResponseHeaders = [new SessionNetworkHeader { Name = "X-Trace", Value = "first" }, new SessionNetworkHeader { Name = "X-Trace", Value = "second" }],
                    ResponseBody = new SessionNetworkBody { Encoding = "utf8", Data = "failure", CapturedBytes = 7, TotalBytes = 7, Truncated = false }
                }]
            };
            var imported = state.ImportSessionSnapshot(snapshot, new Dictionary<string, byte[]> { [$"frame-{sessionId}"] = image });
            Assert.True(imported.IsSuccess, imported.Message);
        }
    }

    private sealed class UnusedAppBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [];
        public bool IsSessionConnected(string sessionId) => false;
        public OperationResult ForceDisconnectSession(string sessionId) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> QueryToolsAsync(string sessionId, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> CallToolAsync(string sessionId, string toolId, JsonObject? arguments, CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null) => throw new NotSupportedException();
    }

    private sealed class UnusedPairingService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration) => throw new NotSupportedException();
    }

    private sealed class EmptyPairingCache : IPairingConfigCache
    {
        public void Add(PairingConfig config) { }
        public void Add(CachedPairingConfig config) { }
        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];
        public CachedPairingConfig? Find(string configId) => null;
        public bool Remove(string configId) => false;
    }
}
