namespace Ansight.Host.Activity;

internal sealed class SessionActivityOperation : IDisposable
{
    private static readonly AsyncLocal<SessionActivityOperation?> current = new();
    private readonly SessionActivityService service;
    private readonly SessionActivityOperation? parent;
    private SessionActivityRecord record;
    private readonly Dictionary<string, string?> targets = new(StringComparer.Ordinal);
    private readonly bool startedWithoutSession;
    private bool completed;
    private bool disposed;
    internal static SessionActivityOperation? Current => current.Value;
    internal bool WasDispatched { get; private set; }
    internal void MarkDispatched() => WasDispatched = true;
    internal bool Owns(string operation, string? requestId) => record.Operation == operation && record.RequestId == requestId;
    internal void SetEvidenceStatus(string status) => record = record with { EvidenceStatus = status };
    internal void SetDeliveryOutcome(string outcome) => record = record with { DeliveryOutcome = outcome };
    internal string OperationId => record.OperationId;
    internal string TraceId => record.TraceId;

    internal SessionActivityOperation(SessionActivityService service, string operation, string category,
        string? sessionId, string? appId, string? requestId)
    {
        this.service = service;
        parent = Current;
        var context = SessionActivityContext.Current ?? new SessionActivityContext(new ActivityCaller());
        var now = DateTimeOffset.UtcNow;
        record = new()
        {
            EventId = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid().ToString("N"),
            TraceId = parent?.TraceId ?? Guid.NewGuid().ToString("N"), ParentOperationId = parent?.OperationId,
            RuntimeId = service.RuntimeId, Phase = "started", TimestampUtc = now, StartedAtUtc = now,
            Operation = ActivityCaller.Limit(operation) ?? "unknown", Category = category,
            SessionId = ActivityCaller.Limit(sessionId), AppId = ActivityCaller.Limit(appId), Caller = context.Caller,
            RequestId = ActivityCaller.Limit(requestId), RunId = context.RunId, BatchRunId = context.BatchRunId
        };
        startedWithoutSession = record.SessionId is null;
        if (record.SessionId is not null) targets[record.SessionId] = record.AppId;
        service.Append(record);
        current.Value = this;
    }

    internal void SetTarget(string sessionId, string? appId = null)
    {
        sessionId = ActivityCaller.Limit(sessionId)!;
        if (targets.TryGetValue(sessionId, out var knownApp) && (appId is null || knownApp == appId)) return;
        targets[sessionId] = ActivityCaller.Limit(appId) ?? knownApp;
        record = record with { SessionId = sessionId, AppId = targets[sessionId] };
        service.Append(record with { EventId = Guid.NewGuid().ToString("N"), Phase = "target", TimestampUtc = DateTimeOffset.UtcNow });
    }

    internal void AddEvidence(string? evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId) || record.EvidenceIds.Count >= 16) return;
        record = record with { EvidenceIds = record.EvidenceIds.Append(ActivityCaller.Limit(evidenceId)!).Distinct().ToArray() };
    }

    internal void Complete(string outcome, string? evidenceStatus = null, string? errorType = null)
    {
        if (completed) return;
        completed = true;
        var completion = record with
        {
            Phase = "completed", TimestampUtc = DateTimeOffset.UtcNow,
            Outcome = outcome, EvidenceStatus = evidenceStatus ?? record.EvidenceStatus, ErrorType = errorType
        };
        foreach (var target in targets)
            service.Append(completion with { EventId = Guid.NewGuid().ToString("N"), SessionId = target.Key, AppId = target.Value });
        if (startedWithoutSession)
            service.Append(completion with { EventId = Guid.NewGuid().ToString("N"), SessionId = null, AppId = null });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // A missing explicit outcome is never interpreted as success, including after an unexpected exit path.
        if (!completed) Complete("unknown");
        current.Value = parent;
    }
}
