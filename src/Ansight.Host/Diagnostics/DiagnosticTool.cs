using System.Text.Json.Nodes;
using Ansight.Infrastructure.Dependencies;

namespace Ansight.Host.Diagnostics;

public sealed record DiagnosticTool(
    string Name, string Status, string? Version, string? Path, string? ResolvedPath,
    string SelectedVia, string? InstallationRoot, string? InstallationSource,
    IReadOnlyList<string> OtherPaths, string UsedBy)
{
    public DependencyVersionAdvice? VersionAdvice => Status == "available"
        ? DependencyBaseline.Compare(Name, Version) : null;
}
