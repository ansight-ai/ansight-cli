using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiSelectorMatchResult(
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiSelectorMatchResult NoMatch { get; } = new(false, 0, "no-match");
}
