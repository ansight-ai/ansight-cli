namespace Ansight.Host.Activity;

/// <summary>Metadata only. Arguments, tool responses, error messages, prompts and credentials are never stored here.</summary>
public sealed record SessionActivityRecord
{
    public string Schema { get; init; } = "ansight.session-activity/v1";
    public required string EventId { get; init; }
    public required string OperationId { get; init; }
    public required string TraceId { get; init; }
    public string? ParentOperationId { get; init; }
    public required string RuntimeId { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required string Operation { get; init; }
    public required string Category { get; init; }
    public string? SessionId { get; init; }
    public string? AppId { get; init; }
    public required ActivityCaller Caller { get; init; }
    public string? RequestId { get; init; }
    public string? RunId { get; init; }
    public string? BatchRunId { get; init; }
    public string? Outcome { get; init; }
    public string? DeliveryOutcome { get; init; }
    public string? ErrorType { get; init; }
    public string? EvidenceStatus { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    public string Provenance { get; init; } = "local";
    public bool HistoryIncomplete { get; init; }
}
