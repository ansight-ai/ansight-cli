using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiSelectorFieldMatch(
    string Name,
    LiveUiStringFieldMatchResult Result);
