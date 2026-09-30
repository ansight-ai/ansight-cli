using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation.Navigation;

internal sealed record NavigationController(
    string Framework,
    IReadOnlyList<string> NavigationToolIds,
    IReadOnlyList<string> VisualTreeToolIds);
