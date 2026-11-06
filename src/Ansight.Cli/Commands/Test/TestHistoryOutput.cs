using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestHistoryOutput(
    string Schema,
    string? AppId,
    string? WorkspacePath,
    WorkspaceTestHistoryResult History);
