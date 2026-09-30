using System.Text.Json.Nodes;

namespace Ansight.Host.Diagnostics;

public sealed record SystemReport(
    string Schema,
    DateTimeOffset CapturedUtc,
    bool IncludesSecretMetadata,
    IReadOnlyDictionary<string, SystemReportSection> Sections,
    IReadOnlyList<string> Findings)
{
    public bool IsComplete => Sections.Values.All(section => section.Status is "complete" or "not-requested" or "not-applicable");
}

public sealed record SystemReportSection(string Status, JsonNode? Data = null, string? Message = null);

public sealed record DiagnosticProcessResult(string Status, string? Output, int? ExitCode);

public sealed record DiagnosticTool(
    string Name, string Status, string? Version, string? Path, string? ResolvedPath,
    string SelectedVia, string? InstallationRoot, string? InstallationSource,
    IReadOnlyList<string> OtherPaths, string UsedBy);
