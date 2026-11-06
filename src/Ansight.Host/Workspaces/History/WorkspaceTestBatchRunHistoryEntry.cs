using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestBatchRunHistoryEntry(
    WorkspaceTestBatchRunAudit Audit,
    string FilePath);
