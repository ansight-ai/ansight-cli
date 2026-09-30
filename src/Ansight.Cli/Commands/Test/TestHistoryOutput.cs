using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestHistoryOutput(
    string Schema,
    string? AppId,
    string? WorkspacePath,
    WorkspaceTestHistoryResult History);

internal sealed record TestHistoryInspectionOutput(
    string Schema,
    WorkspaceTestHistoryInspection Inspection);

internal sealed record TestHistoryExportOutput(
    string Schema,
    WorkspaceTestTraceExportResult Export);

internal sealed record TestMeteringRecoveryOutput(
    string Schema,
    string AuditFilePath,
    string RunId,
    int ModelPassCount,
    SimulatorAgentTokenUsage Tokens,
    SimulatorAgentRunCost? Cost);
