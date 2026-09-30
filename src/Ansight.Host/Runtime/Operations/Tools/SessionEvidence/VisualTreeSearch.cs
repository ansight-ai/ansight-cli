using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class VisualTreeSearch
{
    public static JsonArray Search(AppSessionSnapshot session, VisualTreeSearchCriteria criteria)
    {
        var matches = new List<JsonNode?>();
        foreach (var snapshot in session.VisualTreeSnapshots
                     .Where(snapshot => string.IsNullOrWhiteSpace(criteria.SnapshotId)
                                        || string.Equals(snapshot.SnapshotId, criteria.SnapshotId, StringComparison.Ordinal))
                     .OrderBy(snapshot => snapshot.CapturedAtUtc)
                     .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal))
        {
            if (snapshot.Payload["root"] is not JsonObject root)
            {
                continue;
            }

            var typeRegistry = VisualTreeTypeRegistry.FromPayload(snapshot.Payload);
            SearchNode(snapshot, root, criteria, typeRegistry, depth: 0, path: string.Empty, matches);
            if (matches.Count >= criteria.Limit)
            {
                break;
            }
        }

        return PayloadJson.CreateJsonArray(matches.Take(criteria.Limit));
    }

    private static void SearchNode(
        SessionVisualTreeSnapshot snapshot,
        JsonObject node,
        VisualTreeSearchCriteria criteria,
        VisualTreeTypeRegistry typeRegistry,
        int depth,
        string path,
        ICollection<JsonNode?> matches)
    {
        if (matches.Count >= criteria.Limit)
        {
            return;
        }

        var nodeId = ReadNodeString(node, "id");
        var currentPath = string.IsNullOrWhiteSpace(path)
            ? nodeId ?? "root"
            : $"{path}/{nodeId ?? depth.ToString(CultureInfo.InvariantCulture)}";
        if (MatchesNode(node, criteria, typeRegistry))
        {
            matches.Add(BuildMatchPayload(snapshot, node, typeRegistry, depth, currentPath));
        }

        if (node["children"] is not JsonArray children)
        {
            return;
        }

        foreach (var child in children.OfType<JsonObject>())
        {
            SearchNode(snapshot, child, criteria, typeRegistry, depth + 1, currentPath, matches);
        }
    }

    private static bool MatchesNode(
        JsonObject node,
        VisualTreeSearchCriteria criteria,
        VisualTreeTypeRegistry typeRegistry)
    {
        var hasMatch = !criteria.HasTextFilter && !criteria.HasPoint;
        hasMatch |= ContainsText(node, typeRegistry, criteria.Query);
        hasMatch |= MatchesExact(node, "id", criteria.NodeId);
        hasMatch |= MatchesExact(node, "label", criteria.Label);
        hasMatch |= MatchesExact(node, "automationId", criteria.AutomationId);
        hasMatch |= MatchesExact(typeRegistry.Resolve(node), criteria.Type);

        if (criteria.HasPoint)
        {
            hasMatch |= TryReadNormalizedBounds(node, out var bounds)
                        && criteria.NormalizedX!.Value >= bounds.X
                        && criteria.NormalizedX.Value <= bounds.X + bounds.Width
                        && criteria.NormalizedY!.Value >= bounds.Y
                        && criteria.NormalizedY.Value <= bounds.Y + bounds.Height;
        }

        return hasMatch;
    }

    private static JsonObject BuildMatchPayload(
        SessionVisualTreeSnapshot snapshot,
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry,
        int depth,
        string path)
    {
        var payload = new JsonObject
        {
            ["snapshotId"] = snapshot.SnapshotId,
            ["capturedAtUtc"] = snapshot.CapturedAtUtc,
            ["depth"] = depth,
            ["path"] = path,
            ["nodeId"] = ReadNodeString(node, "id"),
            ["type"] = typeRegistry.Resolve(node),
            ["label"] = ReadNodeString(node, "label"),
            ["automationId"] = ReadNodeString(node, "automationId"),
            ["childCount"] = ReadNodeInteger(node, "childCount")
        };

        if (TryReadNormalizedBounds(node, out var bounds))
        {
            payload["bounds"] = bounds.ToPayload();
        }

        return payload;
    }

    private static bool ContainsText(
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var normalizedQuery = query.Trim();
        return Contains(ReadNodeString(node, "id"), normalizedQuery)
               || Contains(typeRegistry.Resolve(node), normalizedQuery)
               || Contains(ReadNodeString(node, "label"), normalizedQuery)
               || Contains(ReadNodeString(node, "automationId"), normalizedQuery);
    }

    private static bool MatchesExact(JsonObject node, string propertyName, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(ReadNodeString(node, propertyName), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesExact(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string query)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string? ReadNodeString(JsonObject node, string propertyName)
        => node[propertyName]?.GetValue<string>();

    private static int? ReadNodeInteger(JsonObject node, string propertyName)
    {
        if (node[propertyName] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<int>(out var parsed)
            || int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : null;
    }

    private static bool TryReadNormalizedBounds(JsonObject node, out NormalizedBounds bounds)
    {
        bounds = default;
        if (node["normalizedBounds"] is JsonObject normalizedBounds
            && TryReadBoundsObject(normalizedBounds, out bounds))
        {
            return true;
        }

        return node["bounds"] is JsonObject boundsObject
               && TryReadBoundsObject(boundsObject, out bounds);
    }

    private static bool TryReadBoundsObject(JsonObject boundsObject, out NormalizedBounds bounds)
    {
        bounds = default;
        var xNode = boundsObject["x"] ?? boundsObject["absoluteX"];
        var yNode = boundsObject["y"] ?? boundsObject["absoluteY"];
        var widthNode = boundsObject["width"] ?? boundsObject["absoluteWidth"];
        var heightNode = boundsObject["height"] ?? boundsObject["absoluteHeight"];
        if (!TryReadDouble(xNode, out var x)
            || !TryReadDouble(yNode, out var y)
            || !TryReadDouble(widthNode, out var width)
            || !TryReadDouble(heightNode, out var height)
            || width <= 0d
            || height <= 0d)
        {
            return false;
        }

        bounds = new NormalizedBounds(
            Math.Clamp(x, 0d, 1d),
            Math.Clamp(y, 0d, 1d),
            Math.Clamp(width, 0d, 1d),
            Math.Clamp(height, 0d, 1d));
        return true;
    }

    private static bool TryReadDouble(JsonNode? node, out double value)
    {
        value = 0d;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        return jsonValue.TryGetValue<double>(out value)
               || double.TryParse(jsonValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
