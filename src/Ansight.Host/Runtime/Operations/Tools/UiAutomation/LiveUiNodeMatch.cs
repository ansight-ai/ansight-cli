using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiNodeMatch(
    JsonObject Node,
    IReadOnlyList<JsonObject> Ancestors,
    VisualTreeTypeRegistry TypeRegistry);
