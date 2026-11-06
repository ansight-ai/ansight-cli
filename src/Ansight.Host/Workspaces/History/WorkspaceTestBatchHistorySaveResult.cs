using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestBatchHistorySaveResult(
    WorkspaceTestBatchRunAudit Audit,
    string? FilePath,
    string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}
