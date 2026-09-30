using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class RemoteAppToolCatalog
{
    public static bool HasTool(JsonNode? catalogPayload, string toolId)
    {
        return catalogPayload is JsonObject catalogObject
               && catalogObject["tools"] is JsonArray tools
               && tools.OfType<JsonObject>().Any(tool => ToolIdMatches(tool, toolId));
    }

    private static bool ToolIdMatches(JsonObject tool, string toolId)
    {
        var id = tool["id"]?.GetValue<string>() ?? tool["name"]?.GetValue<string>();
        return string.Equals(id, toolId, StringComparison.Ordinal);
    }
}
