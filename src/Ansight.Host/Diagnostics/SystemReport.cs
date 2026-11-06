using System.Text.Json.Nodes;
using Ansight.Infrastructure.Dependencies;

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
