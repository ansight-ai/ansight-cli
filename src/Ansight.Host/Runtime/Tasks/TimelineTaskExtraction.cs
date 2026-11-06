using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>
/// A reviewable repository task draft extracted from a historical session range.
/// </summary>
public sealed record TimelineTaskExtraction(
    string SuggestedName,
    string Source,
    int GestureCount,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<TimelineExtractedAction> Actions { get; init; } = [];

    public IReadOnlyList<ReplayStep> ReplaySteps { get; init; } = [];

    public IReadOnlyList<string> ReplayInstructions => ReplaySteps
        .Select(static step => step.Instruction)
        .ToArray();

    public string Summary => Diagnostics.Count == 0
        ? $"Generated {GeneratedActionCount:N0} replay action(s) from captured interaction evidence."
        : $"Generated {GeneratedActionCount:N0} replay action(s) from captured interaction evidence · "
            + $"{Diagnostics.Count:N0} review item(s).";
}
