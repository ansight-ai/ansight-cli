namespace Ansight.Cli.Commands.Workspace;

internal sealed record WorkspaceListOutput(
    string Schema,
    IReadOnlyList<LinkedWorkspaceDescriptor> Workspaces);

internal sealed record LinkedWorkspaceDescriptor(
    string AppId,
    string AppName,
    string CodebasePath,
    bool AutomaticTrendsMonitoringEnabled,
    bool RepositoryAutomationsEnabled);
