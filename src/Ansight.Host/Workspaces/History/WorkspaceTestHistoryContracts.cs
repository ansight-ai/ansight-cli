using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public static class WorkspaceTestHistoryStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Pending = "pending";
    public const string Skipped = "skipped";
}

public sealed record WorkspaceTestBatchItemAudit(
    int Index,
    string TestId,
    string TestName,
    string AppId,
    string Status,
    string Message,
    string? AgentRunId,
    string? SessionId,
    WorkspaceTestTarget? Target,
    string? AgentAuditFilePath,
    long DurationMilliseconds,
    int TotalTokens,
    int ModelPassCount,
    int AnsightToolCallCount)
{
    public string? RequestedDeviceIdentifier { get; init; }

    public static WorkspaceTestBatchItemAudit Pending(
        int index,
        WorkspaceTestDefinition test,
        WorkspaceTestTargetRequest? requestedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        return new WorkspaceTestBatchItemAudit(
            index,
            test.TestId,
            test.Name,
            test.AppId,
            WorkspaceTestHistoryStatuses.Pending,
            "Waiting to run.",
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0)
        {
            RequestedDeviceIdentifier = requestedTarget?.DeviceIdentifier
        };
    }

    public static WorkspaceTestBatchItemAudit FromResult(
        int index,
        WorkspaceTestDefinition test,
        WorkspaceTestRunResult result,
        WorkspaceTestTargetRequest? requestedTarget = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(result);
        var audit = result.AgentResult?.Audit;
        var status = result.IsSkipped
            ? WorkspaceTestHistoryStatuses.Skipped
            : audit?.Status == SimulatorAgentRunStatus.Cancelled
            ? WorkspaceTestHistoryStatuses.Cancelled
            : result.IsSuccess
                ? WorkspaceTestHistoryStatuses.Succeeded
                : WorkspaceTestHistoryStatuses.Failed;
        return new WorkspaceTestBatchItemAudit(
            index,
            test.TestId,
            test.Name,
            test.AppId,
            status,
            result.Message,
            audit?.RunId,
            result.SessionId,
            result.Target,
            result.AgentResult?.AuditFilePath,
            audit?.DurationMilliseconds ?? 0,
            audit?.Tokens.TotalTokens ?? 0,
            audit?.ModelPassCount ?? 0,
            audit?.AnsightToolCallCount ?? 0)
        {
            RequestedDeviceIdentifier = requestedTarget?.DeviceIdentifier
        };
    }

    public static WorkspaceTestBatchItemAudit FromAgentAudit(
        int index,
        WorkspaceTestDefinition test,
        SimulatorAgentRunAudit audit,
        string? auditFilePath,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(audit);
        return new WorkspaceTestBatchItemAudit(
            index,
            test.TestId,
            test.Name,
            test.AppId,
            audit.Status switch
            {
                SimulatorAgentRunStatus.Succeeded => WorkspaceTestHistoryStatuses.Succeeded,
                SimulatorAgentRunStatus.Cancelled => WorkspaceTestHistoryStatuses.Cancelled,
                _ => WorkspaceTestHistoryStatuses.Failed
            },
            string.IsNullOrWhiteSpace(message) ? audit.Message : message.Trim(),
            audit.RunId,
            audit.SessionId,
            null,
            auditFilePath,
            audit.DurationMilliseconds,
            audit.Tokens.TotalTokens,
            audit.ModelPassCount,
            audit.AnsightToolCallCount);
    }
}

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

public sealed record WorkspaceTestBatchRunHistoryEntry(
    WorkspaceTestBatchRunAudit Audit,
    string FilePath);

public sealed record WorkspaceTestBatchHistorySaveResult(
    WorkspaceTestBatchRunAudit Audit,
    string? FilePath,
    string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}

public sealed record WorkspaceTestRunHistorySummary(
    string RunId,
    string? BatchRunId,
    string? WorkspacePath,
    string? TestId,
    string? TestName,
    string DisplayName,
    string? AppId,
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    string Status,
    string Message,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMilliseconds,
    int TotalTokens,
    int ModelPassCount,
    int AnsightToolCallCount,
    string FilePath)
{
    public string? Reasoning { get; init; }

    public SimulatorAgentRunCost? CalculatedCost { get; init; }
}

public sealed record WorkspaceTestHistoryResult(
    string HistoryDirectoryPath,
    IReadOnlyList<WorkspaceTestBatchRunHistoryEntry> Batches,
    IReadOnlyList<WorkspaceTestRunHistorySummary> Runs);

public sealed record WorkspaceTestHistoryInspection(
    string RequestedRunId,
    WorkspaceTestBatchRunHistoryEntry? Batch,
    IReadOnlyList<SimulatorAgentRunHistoryEntry> Runs);
