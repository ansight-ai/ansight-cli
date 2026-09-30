using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestCatalogOutput(
    string Schema,
    string WorkspacePath,
    IReadOnlyList<WorkspaceTestDefinition> Tests,
    IReadOnlyList<string> Warnings,
    bool IsValid);
