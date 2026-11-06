using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiStringFieldMatchResult(
    bool Applies,
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiStringFieldMatchResult NotApplicable { get; } = new(false, true, 1, "not-applicable");

    public static LiveUiStringFieldMatchResult NoMatch { get; } = new(true, false, 0, "no-match");
}
