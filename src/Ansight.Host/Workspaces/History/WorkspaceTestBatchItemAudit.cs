using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

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
    public string? TaskRunId { get; init; }

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

        var audit = result.RunAudit;
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
            result.AgentResult?.Audit.RunId,
            result.SessionId,
            result.Target,
            result.AgentResult?.AuditFilePath ?? result.TaskAuditFilePath,
            audit?.DurationMilliseconds ?? 0,
            audit?.Tokens.TotalTokens ?? 0,
            audit?.ModelPassCount ?? 0,
            audit?.AnsightToolCallCount ?? 0)
        {
            RequestedDeviceIdentifier = requestedTarget?.DeviceIdentifier,
            TaskRunId = result.TaskResult?.RunId
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
