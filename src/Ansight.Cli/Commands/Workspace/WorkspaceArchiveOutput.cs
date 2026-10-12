using Ansight.Host.Workspaces.Archives;

namespace Ansight.Cli.Commands.Workspace;

internal sealed record WorkspaceArchiveOutput(
    string Schema,
    string Action,
    WorkspaceArchiveResult Result,
    string? AppId = null);
