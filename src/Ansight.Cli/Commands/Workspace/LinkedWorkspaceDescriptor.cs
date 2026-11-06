namespace Ansight.Cli.Commands.Workspace;

internal sealed record LinkedWorkspaceDescriptor(
    string AppId,
    string AppName,
    string CodebasePath,
    bool AutomaticTrendsMonitoringEnabled,
    bool RepositoryAutomationsEnabled);
