namespace Ansight.Cli.Commands.Workspace;

internal sealed record WorkspaceListOutput(
    string Schema,
    IReadOnlyList<LinkedWorkspaceDescriptor> Workspaces);
