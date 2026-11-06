using System.Globalization;
using System.Text;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiStringMatchResult(
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiStringMatchResult NoMatch { get; } = new(false, 0, "no-match");
}
