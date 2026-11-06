using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestHistoryInspection(
    string RequestedRunId,
    WorkspaceTestBatchRunHistoryEntry? Batch,
    IReadOnlyList<SimulatorAgentRunHistoryEntry> Runs);
