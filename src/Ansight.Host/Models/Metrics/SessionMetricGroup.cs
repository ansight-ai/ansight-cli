using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Models.Metrics;

public sealed record SessionMetricGroup(string Id, string Name)
{
    internal static SessionMetricGroup? Read(JsonElement channel)
    {
        var hasGroup = channel.TryGetProperty("metricGroup", out var group)
            && group.ValueKind == JsonValueKind.Object;

        if (!hasGroup)
        {
            return null;
        }

        var id = ReadString(group, "id");
        var name = ReadString(group, "name");

        if (id is null || name is null)
        {
            return null;
        }

        return new SessionMetricGroup(id, name);
    }

    internal JsonObject ToPayload()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name
        };
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }
}
