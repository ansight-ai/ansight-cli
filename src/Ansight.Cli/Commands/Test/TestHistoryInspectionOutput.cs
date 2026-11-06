using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestHistoryInspectionOutput(
    string Schema,
    WorkspaceTestHistoryInspection Inspection);
