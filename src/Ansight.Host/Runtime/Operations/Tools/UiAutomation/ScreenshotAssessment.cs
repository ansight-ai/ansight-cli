using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class ScreenshotAssessment
{
    public static JsonArray ValidateCriteria(JsonNode? node)
    {
        if (node is not JsonArray criteria || criteria.Count is < 1 or > 16)
            throw new ArgumentException("criteria must contain 1 to 16 criteria.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in criteria)
        {
            var criterion = item as JsonObject ?? throw new ArgumentException("Each criterion must be an object.");
            var id = RequiredText(criterion, "id", 80);
            RequiredText(criterion, "description", 2_000);
            if (!ids.Add(id)) throw new ArgumentException("Criterion IDs must be unique.");
            if (criterion["count"] is JsonNode countNode)
            {
                var count = countNode as JsonObject ?? throw new ArgumentException("count must be an object.");
                RequiredText(count, "label", 80);
                if (count["equals"] is not JsonValue value || !value.TryGetValue<int>(out var expected) || expected is < 0 or > 64)
                    throw new ArgumentException("count.equals must be an integer from 0 to 64.");
                if (count["attributes"] is not null) ValidateAttributes(count["attributes"]);
            }
        }
        return criteria.DeepClone().AsArray();
    }

    public static JsonObject Parse(ScreenshotEvaluationResponse response, InspectionImage actual, JsonArray requested)
    {
        try { return ParseCore(response.Text, actual, requested); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            var criteria = new JsonArray();
            foreach (var criterion in requested)
                criteria.Add(new JsonObject { ["id"] = criterion!["id"]!.GetValue<string>(), ["verdict"] = "inconclusive",
                    ["explanation"] = "The model did not return valid, complete image evidence.", ["regionIds"] = new JsonArray() });
            return new JsonObject { ["passed"] = false, ["verdict"] = "inconclusive", ["criteria"] = criteria,
                ["regions"] = new JsonArray(), ["objects"] = new JsonArray(),
                ["message"] = "Screenshot assessment was inconclusive: invalid or incomplete model evidence." };
        }
    }

    private static JsonObject ParseCore(string text, InspectionImage actual, JsonArray requested)
    {
        if (text.Length > 128_000) throw new ArgumentException("Model response is too large.");
        var document = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("Expected JSON object.");
        var objects = document["objects"] as JsonArray ?? throw new ArgumentException("Missing objects.");
        if (objects.Count > ScreenshotComparison.MaximumRegions) throw new ArgumentException("Too many objects.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var regions = new JsonArray();
        foreach (var item in objects)
        {
            var obj = item as JsonObject ?? throw new ArgumentException("Invalid object.");
            var id = RequiredText(obj, "id", 64);
            if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$") || !ids.Add(id)) throw new ArgumentException("Invalid or duplicate object id.");
            var label = RequiredText(obj, "label", 160);
            ValidateAttributes(obj["attributes"]);
            var bounds = obj["bounds"] as JsonObject ?? throw new ArgumentException("Missing object bounds.");
            var x = Coordinate(bounds, "x"); var y = Coordinate(bounds, "y");
            var width = Coordinate(bounds, "width"); var height = Coordinate(bounds, "height");
            if (x >= 1 || y >= 1 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
                throw new ArgumentException("Object bounds must fit inside ACTUAL.");
            regions.Add(ScreenshotInspectionEvidence.Region(id, label, "semantic", actual.FrameId!, x, y, width, height));
        }
        var returned = document["criteria"] as JsonArray ?? throw new ArgumentException("Missing criteria.");
        if (returned.Count != requested.Count) throw new ArgumentException("Incomplete criteria.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var criteria = new JsonArray();
        foreach (var item in returned)
        {
            var criterion = item as JsonObject ?? throw new ArgumentException("Invalid criterion.");
            var id = RequiredText(criterion, "id", 80);
            var original = requested.OfType<JsonObject>().SingleOrDefault(value => value["id"]!.GetValue<string>() == id);
            if (original is null || !seen.Add(id)) throw new ArgumentException("Unexpected or duplicate criterion.");
            var verdict = RequiredText(criterion, "verdict", 16);
            if (verdict is not ("pass" or "fail" or "inconclusive")) throw new ArgumentException("Invalid verdict.");
            var explanation = RequiredText(criterion, "explanation", 2_000);
            if (criterion["regionIds"] is not JsonArray links || links.Count > 64
                || links.Any(link => link is null || !ids.Contains(link.GetValue<string>())))
                throw new ArgumentException("Criterion contains invalid region references.");
            var result = new JsonObject { ["id"] = id, ["description"] = original["description"]!.DeepClone(),
                ["verdict"] = verdict, ["explanation"] = explanation, ["regionIds"] = links.DeepClone() };
            if (original["count"] is JsonObject rule)
            {
                var matches = objects.OfType<JsonObject>().Where(obj => Matches(obj, rule)).ToArray();
                var count = matches.Length;
                var expected = rule["equals"]!.GetValue<int>();
                result["observedCount"] = count; result["expectedCount"] = expected;
                result["regionIds"] = new JsonArray(matches.Select(obj => obj["id"]!.DeepClone()).ToArray());
                // Never promote uncertain or failed visual evidence to a pass merely because a count matches.
                if (verdict != "inconclusive" && count != expected) result["verdict"] = "fail";
                result["explanation"] = explanation + $" Detected {count}; expected {expected}.";
            }
            criteria.Add(result);
        }
        var overall = criteria.Any(c => c!["verdict"]!.GetValue<string>() == "fail") ? "fail"
            : criteria.Any(c => c!["verdict"]!.GetValue<string>() == "inconclusive") ? "inconclusive" : "pass";
        return new JsonObject { ["passed"] = overall == "pass", ["verdict"] = overall,
            ["criteria"] = criteria, ["objects"] = objects.DeepClone(), ["regions"] = regions,
            ["message"] = $"Screenshot assessment: {overall}." };
    }

    private static bool Matches(JsonObject obj, JsonObject rule) =>
        string.Equals(obj["label"]!.GetValue<string>(), rule["label"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)
        && (rule["attributes"] is not JsonObject attributes || attributes.All(attribute =>
            string.Equals(obj["attributes"]?[attribute.Key]?.GetValue<string>(), attribute.Value!.GetValue<string>(), StringComparison.OrdinalIgnoreCase)));

    private static void ValidateAttributes(JsonNode? node)
    {
        if (node is not JsonObject attributes || attributes.Count > 16) throw new ArgumentException("Invalid object attributes.");
        foreach (var field in attributes)
        {
            if (field.Key.Length is < 1 or > 80) throw new ArgumentException("Invalid attribute name.");
            RequiredText(attributes, field.Key, 160);
        }
    }

    private static string RequiredText(JsonObject obj, string name, int limit)
    {
        if (obj[name] is not JsonValue value || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text) || text.Length > limit) throw new ArgumentException($"{name} must be non-empty text (at most {limit} characters).");
        return text;
    }

    private static double Coordinate(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value || !value.TryGetValue<double>(out var number)
            || !double.IsFinite(number) || number < 0 || number > 1) throw new ArgumentException("Invalid normalized coordinate.");
        return number;
    }
}
