using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestBatchRunAudit(
    int SchemaVersion,
    string BatchRunId,
    string Source,
    string WorkspacePath,
    IReadOnlyList<string> RequestedTestIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    int MaximumTurnsPerInstruction,
    int MaximumRoundTrips,
    int MaximumToolCalls,
    bool ContinueAfterTestFailure,
    bool WorkspaceToolsEnabled,
    WorkspaceTestTargetRequest? RequestedTarget,
    Guid? TeamId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string Status,
    string Message,
    IReadOnlyList<WorkspaceTestBatchItemAudit> Tests)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    public IReadOnlyList<WorkspaceTestTargetRequest>? RequestedTargets { get; init; }
    public bool Parallel { get; init; }
    public int PassedCount => Tests.Count(static test =>
        test.Status == WorkspaceTestHistoryStatuses.Succeeded);

    public int FailedCount => Tests.Count(static test =>
        test.Status == WorkspaceTestHistoryStatuses.Failed);

    public int CancelledCount => Tests.Count(static test =>
        test.Status == WorkspaceTestHistoryStatuses.Cancelled);

    public int SkippedCount => Tests.Count(static test =>
        test.Status == WorkspaceTestHistoryStatuses.Skipped);

    public long DurationMilliseconds => CompletedUtc.HasValue
        ? Math.Max(0, (long)(CompletedUtc.Value - StartedUtc).TotalMilliseconds)
        : Math.Max(0, (long)(DateTimeOffset.UtcNow - StartedUtc).TotalMilliseconds);

    public int TotalTokens => Tests.Sum(static test => test.TotalTokens);
}
