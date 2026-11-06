using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Sanitization;

internal static class SanitizationJson
{
    internal static void VisitObjects(JsonNode node, Action<JsonObject> visitor)
    {
        if (node is JsonObject jsonObject)
        {
            visitor(jsonObject);

            foreach (var child in jsonObject.Select(static property => property.Value).OfType<JsonNode>())
            {
                VisitObjects(child, visitor);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<JsonNode>())
            {
                VisitObjects(child, visitor);
            }
        }
    }

    internal static SessionSanitizationRegion? ReadBounds(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node["bounds"] is JsonArray array)
        {
            var offset = array.Count >= 8 ? 4 : 0;
            double arrayX = 0;
            double arrayY = 0;
            double arrayWidth = 0;
            double arrayHeight = 0;
            var hasArrayBounds = TryReadDouble(array, offset, out arrayX)
                && TryReadDouble(array, offset + 1, out arrayY)
                && TryReadDouble(array, offset + 2, out arrayWidth)
                && TryReadDouble(array, offset + 3, out arrayHeight);

            return hasArrayBounds
                ? new SessionSanitizationRegion(arrayX, arrayY, arrayWidth, arrayHeight)
                : null;
        }

        var bounds = node["bounds"] as JsonObject ?? node;
        var absolute = bounds["absoluteX"] is not null;
        double x = 0;
        double y = 0;
        double width = 0;
        double height = 0;
        var hasObjectBounds = TryReadDouble(bounds, absolute ? "absoluteX" : "x", out x)
            && TryReadDouble(bounds, absolute ? "absoluteY" : "y", out y)
            && TryReadDouble(bounds, absolute ? "absoluteWidth" : "width", out width)
            && TryReadDouble(bounds, absolute ? "absoluteHeight" : "height", out height);

        return hasObjectBounds
            ? new SessionSanitizationRegion(x, y, width, height)
            : null;
    }

    internal static bool TryReadDouble(JsonObject node, string propertyName, out double result)
    {
        result = 0;

        return node[propertyName] is JsonValue value && TryReadDouble(value, out result);
    }

    internal static bool TryReadDouble(JsonArray array, int index, out double result)
    {
        result = 0;

        var isValidIndex = index >= 0 && index < array.Count;

        if (!isValidIndex || array[index] is not JsonValue value)
        {
            return false;
        }

        return TryReadDouble(value, out result);
    }

    internal static bool TryReadDouble(JsonValue value, out double result)
    {
        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;

            return true;
        }

        result = 0;

        return false;
    }
}
