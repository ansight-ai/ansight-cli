using System.Text.Json.Nodes;

namespace Ansight.Host.AppGraphs;

public sealed record AppGraphNavigationParadigm(
    string Topology,
    string ChildInventory,
    string Selection,
    string ReturnBehavior,
    string Evidence,
    bool ActiveChildMustBeLast = false)
{
    public JsonObject ToJson() => new()
    {
        ["topology"] = Topology,
        ["childInventory"] = ChildInventory,
        ["selection"] = Selection,
        ["returnBehavior"] = ReturnBehavior,
        ["evidence"] = Evidence,
        ["activeChildMustBeLast"] = ActiveChildMustBeLast
    };
}
