using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Observations;

internal static class ScreenTextObservation
{
    public static string BuildModelOutput(JsonObject content, bool isError, int maximumCharacters)
    {
        var result = new JsonObject();
        foreach (var name in new[]
        {
            "capability", "evidenceSource", "provider", "capturedAtUtc", "screenshotFrameId",
            "screenWidth", "screenHeight", "totalCount", "warning", "message"
        })
        {
            if (content[name] is { } value)
            {
                result[name] = value.DeepClone();
            }
        }

        var entries = new JsonArray();
        result["content"] = entries;
        result["count"] = 0;
        result["projectionSourceCount"] = (content["content"] as JsonArray)?.Count ?? 0;
        result["projectionOmittedCount"] = result["projectionSourceCount"]!.DeepClone();
        result["truncated"] = true;
        result["projectionGuidance"] = "Omitted text is not absent. Use a focused text lookup to inspect an omitted target. OCR tap hints expire after a layout change and are not stable automation IDs.";
        var envelope = new JsonObject { ["isError"] = isError, ["result"] = result };
        if (envelope.ToJsonString().Length > maximumCharacters)
        {
            return new JsonObject
            {
                ["isError"] = isError,
                ["truncated"] = true,
                ["message"] = "OCR metadata exceeds the context budget. Full evidence is retained; use a focused text lookup."
            }.ToJsonString();
        }

        foreach (var entry in (content["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var projected = new JsonObject();
            foreach (var name in new[] { "text", "confidence", "bounds", "tapHint" })
            {
                if (entry[name] is { } value)
                {
                    projected[name] = value.DeepClone();
                }
            }

            // Keep complete text and exact tap selectors. Only display geometry is rounded.
            if (projected["confidence"] is JsonValue confidence && confidence.TryGetValue<double>(out var number))
            {
                projected["confidence"] = Math.Round(number, 3);
            }

            UiNodeProjection.CompactBoundsInPlace(projected);
            entries.Add(projected);
            result["count"] = entries.Count;
            if (envelope.ToJsonString().Length > maximumCharacters - 32)
            {
                entries.RemoveAt(entries.Count - 1);
                result["count"] = entries.Count;
            }
        }

        var sourceCount = result["projectionSourceCount"]!.GetValue<int>();
        var omitted = sourceCount - entries.Count;
        result["projectionOmittedCount"] = omitted;
        result["truncated"] = omitted > 0 || content["truncated"]?.GetValue<bool>() == true;
        return envelope.ToJsonString();
    }
}
