using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestHistoryExportOutput(
    string Schema,
    WorkspaceTestTraceExportResult Export);
