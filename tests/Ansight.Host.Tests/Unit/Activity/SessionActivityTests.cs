using Ansight.Host.Activity;
using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Tests.Unit.Activity;

public sealed class SessionActivityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ansight-activity-tests", Guid.NewGuid().ToString("N"));
    private SessionActivityStore Store(long segmentBytes = 4 * 1024 * 1024, int segmentCount = 8) => new(directory, segmentBytes, segmentCount);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task PersistsBeforeDispatchAndRecordsCancellationWithoutPrivateErrorText()
    {
        var store = Store();
        var service = new SessionActivityService(store);
        using var caller = SessionActivityContext.Push(new(new ActivityCaller { Kind = "agent", Name = "external", ExecutorAttribution = "caller-declared" }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RecordAsync<int>("type", "tool", "session", () =>
        {
            Assert.Equal("started", Assert.Single(store.Read("session").Records).Phase);
            throw new OperationCanceledException("a private input value");
        }, _ => "succeeded"));
        var records = store.Read("session").Records;
        Assert.Equal(2, records.Count);
        Assert.Equal("cancelled", records[1].Outcome);
        Assert.Equal(records[0].OperationId, records[1].OperationId);
        Assert.DoesNotContain("private input", File.ReadAllText(Directory.GetFiles(store.DirectoryFor("session"), "*.jsonl").Single()));
        Assert.Null(SessionActivityOperation.Current);
    }

    [Fact]
    public async Task ParallelRequestsKeepCallerAndParentageIsolatedEvenWithoutDetailedTrace()
    {
        var store = Store();
        var service = new SessionActivityService(store);
        using var noTrace = RepositoryTaskTraceScope.Begin(false);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            using var caller = SessionActivityContext.Push(new(new ActivityCaller { Name = "caller-" + index, PrincipalId = "account-" + index, PrincipalAttribution = "authenticated" }));
            using var run = SessionActivityContext.ExecuteAs("automation", "repository-task", "run-" + index);
            using var parent = service.Begin("task", "task", "session");
            await Task.Yield();
            await service.RecordAsync("child", "tool", "session", async () => { await Task.Yield(); return true; }, _ => "succeeded");
            parent.Complete("succeeded");
        }));
        var records = store.Read("session", limit: 500).Records;
        Assert.Equal(80, records.Count);
        foreach (var child in records.Where(record => record.Operation == "child" && record.Phase == "completed"))
        {
            var parent = Assert.Single(records, record => record.OperationId == child.ParentOperationId && record.Phase == "started");
            Assert.Equal(parent.TraceId, child.TraceId);
            Assert.Equal(parent.Caller, child.Caller);
            Assert.Equal(parent.RunId, child.RunId);
            Assert.Equal("host-generated", child.Caller.ExecutorAttribution);
            Assert.StartsWith("caller-", child.Caller.InitiatorName);
        }
        Assert.Null(SessionActivityContext.Current);
    }

    [Fact]
    public void TornTailIsReportedAndRecoveredWithoutJoiningTheNextRecord()
    {
        var store = Store();
        var service = new SessionActivityService(store);
        using (var operation = service.Begin("first", "tool", "session")) operation.Complete("succeeded");
        var cursor = store.Read("session").NextCursor;
        var file = Directory.GetFiles(store.DirectoryFor("session"), "*.jsonl").Single();
        File.AppendAllText(file, "{\"unfinished\":");
        var torn = store.Read("session", cursor);
        Assert.True(torn.IncompleteTail);
        Assert.Empty(torn.Records);
        using (var operation = new SessionActivityService(Store()).Begin("second", "tool", "session")) operation.Complete("failed");
        var recovered = store.Read("session", cursor);
        Assert.Equal(1, recovered.SkippedRecords);
        Assert.Equal(2, recovered.Records.Count);
        Assert.False(recovered.IncompleteTail);
        Assert.Equal("failed", recovered.Records[^1].Outcome);
    }

    [Fact]
    public void RotationExpiresCursorsAndCompletionRemainsSelfContained()
    {
        var store = Store(segmentBytes: 800, segmentCount: 2);
        var service = new SessionActivityService(store);
        string? cursor;
        using (var first = service.Begin("first", "tool", "session"))
        {
            cursor = store.Read("session").NextCursor;
            first.Complete("succeeded");
        }
        for (var index = 0; index < 8; index++)
            using (var operation = service.Begin("rotate-" + index, "tool", "session")) operation.Complete("succeeded");
        var page = store.Read("session", cursor);
        Assert.True(page.CursorExpired);
        Assert.True(page.RetentionLimited);
        Assert.Equal(2, Directory.GetFiles(store.DirectoryFor("session"), "*.jsonl").Length);
        Assert.Contains(page.Records, record => record.Phase == "completed" && record.Operation == "rotate-7");
    }

    [Fact]
    public void ResolvedMultiSessionOperationsCompleteEveryTargetAndKeepOneTrace()
    {
        var service = new SessionActivityService(Store());
        using (var operation = service.Begin("compare", "tool"))
        {
            operation.SetTarget("left", "app");
            operation.SetTarget("right", "app");
            operation.Complete("succeeded");
        }
        var host = service.Read(null).Records;
        var left = service.Read("left").Records;
        var right = service.Read("right").Records;
        Assert.Equal(new[] { "started", "completed" }, host.Select(record => record.Phase));
        Assert.Equal(new[] { "target", "completed" }, left.Select(record => record.Phase));
        Assert.Equal(new[] { "target", "completed" }, right.Select(record => record.Phase));
        Assert.Equal(host[0].TraceId, right[1].TraceId);
        Assert.Equal(left[0].OperationId, right[1].OperationId);
        Assert.Equal("succeeded", left[1].Outcome);
    }

    [Fact]
    public void PaginationPreservesUnicodeAndDoesNotRepeatEvents()
    {
        var service = new SessionActivityService(Store());
        for (var index = 0; index < 6; index++)
            using (var operation = service.Begin("操作-" + index, "tool", "session")) operation.Complete("succeeded");
        var seen = new HashSet<string>();
        string? cursor = null;
        while (true)
        {
            var page = service.Read("session", cursor, 3);
            foreach (var item in page.Records) Assert.True(seen.Add(item.EventId));
            if (page.Records.Count == 0) break;
            cursor = page.NextCursor;
        }
        Assert.Equal(12, seen.Count);
        Assert.Throws<ArgumentException>(() => service.Read("session", "../../evil:0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Read("session", limit: 501));
    }

    [Fact]
    public async Task UnwritableJournalDoesNotChangeActionResultAndReportsPersistentGap()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session-activity"), "not a directory");
        var service = new SessionActivityService(Store());
        Assert.Equal(42, await service.RecordAsync("action", "tool", "session", () => Task.FromResult(42), _ => "succeeded"));
        Assert.NotNull(service.PersistenceError);
        File.Delete(Path.Combine(directory, "session-activity"));
        using (var operation = service.Begin("later", "tool", "session")) operation.Complete("succeeded");
        Assert.NotNull(service.Read("session").PersistenceError);
    }

    [Fact]
    public void ForwardedClaimsCannotAssertAuthenticationOrHostGeneratedAttribution()
    {
        var caller = ActivityCaller.Declared(new ActivityCaller
        {
            Kind = "agent", Name = "external", PrincipalId = "forged-account", PrincipalAttribution = "authenticated",
            ExecutorAttribution = "host-generated", WorkId = new string('x', 1000)
        });
        Assert.Null(caller.PrincipalId);
        Assert.Equal("unknown", caller.PrincipalAttribution);
        Assert.Equal("caller-declared", caller.ExecutorAttribution);
        Assert.Equal(256, caller.WorkId!.Length);
        Assert.Equal("unknown", ActivityCaller.Declared(null).Kind);
    }

    [Fact]
    public async Task DirectAppToolsRecordRejectedDeliveryWithoutArguments()
    {
        var service = new SessionActivityService(Store());
        var tools = new AppToolService(new TestAppToolBridge(), _ => true, service);
        var result = await tools.CallAsync("session", "private.tool", new JsonObject { ["password"] = "sensitive-argument" });
        Assert.False(result.Success);
        var record = service.Read("session").Records.Last();
        Assert.Equal("private.tool", record.Operation);
        Assert.Equal("failed", record.Outcome);
        Assert.DoesNotContain("sensitive-argument", JsonSerializer.Serialize(record));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryTriggersRecordTrustedExecutorAndRejectedOutcome(bool typeScript)
    {
        var service = new SessionActivityService(Store());
        var executor = new RejectedAutomationExecutor(service);
        var dispatcher = new DispatchingRepositoryAutomationExecutor(executor, executor, service);
        var registration = new RepositoryAutomationRegistration(directory, "capture", typeScript ? RepositoryAutomationActionKind.TypeScript : RepositoryAutomationActionKind.AppTool, "capture.ts", "capture", null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), RepositoryAutomationRetryPolicy.None);
        var trigger = new RepositoryAutomationTriggerDefinition(directory, "trigger", 1, "app", "event", null, registration, []);
        var request = new RepositoryAutomationExecutionRequest("run", trigger, new AutomationEventEnvelope
        {
            EventId = "event", Kind = "event", AppId = "app", SessionId = "session", CorrelationId = "request",
            OccurredAtUtc = DateTimeOffset.UtcNow
        }, DateTimeOffset.UtcNow);
        using var caller = SessionActivityContext.Push(new(ActivityCaller.Declared(new() { Kind = "human", Name = "initiator" })));
        await dispatcher.ExecuteAsync(request, CancellationToken.None);
        var completed = service.Read("session").Records.Last();
        Assert.Equal("rejected", completed.Outcome);
        Assert.Equal("automation", completed.Caller.Kind);
        Assert.Equal("host-generated", completed.Caller.ExecutorAttribution);
        Assert.Equal("unknown", completed.Caller.InitiatorKind);
        Assert.Null(completed.Caller.InitiatorName);
        Assert.Null(completed.Caller.PrincipalId);
        Assert.Equal("run", completed.RunId);
        Assert.Equal("request", completed.RequestId);
        Assert.Equal("human", SessionActivityContext.Current!.Caller.Kind);
    }

    private sealed class RejectedAutomationExecutor(SessionActivityService activity) : IRepositoryAutomationExecutor
    {
        public Task<RepositoryAutomationExecutionResult> ExecuteAsync(RepositoryAutomationExecutionRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal("started", Assert.Single(activity.Read(request.Event.SessionId).Records).Phase);
            return Task.FromResult(new RepositoryAutomationExecutionResult(AutomationRunStatus.Rejected,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "Denied", null, ""));
        }
    }

    private sealed class TestAppToolBridge : IAppToolBridge
    {
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IReadOnlyList<string> GetConnectedSessionIds() => [];
        public bool IsSessionConnected(string sessionId) => false;
        public OperationResult ForceDisconnectSession(string sessionId) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> QueryToolsAsync(string sessionId, CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null) => throw new NotSupportedException();
        public Task<AppToolBridgeResponse> CallToolAsync(string sessionId, string toolId, JsonObject? arguments,
            CancellationToken cancellationToken, AppToolBridgeRequestContext? requestContext = null) => throw new NotSupportedException();
    }
}
