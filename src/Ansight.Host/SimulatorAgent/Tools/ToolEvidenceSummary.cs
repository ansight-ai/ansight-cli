using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent;

/// <summary>Keeps earlier verification evidence when large tool responses leave HTTP history.</summary>
internal static class ToolEvidenceSummary
{
    private const int MaximumCharacters = 10_000;
    private const int MaximumTextCharacters = 400;

    public static JsonObject? Create(JsonObject output) => Create(output, MaximumCharacters);

    private static JsonObject? Create(JsonObject output, int maximumCharacters)
    {
        var result = output["result"] as JsonObject ?? output;
        var evidence = new JsonObject();
        var truncated = false;
        foreach (var name in new[] { "isError", "taskId", "status", "capability", "performed", "condition", "satisfied", "passed", "matchCount", "totalMatches", "capturedAtUtc" })
        {
            CopyScalar(evidence, result, name);
        }

        foreach (var name in new[] { "selector", "selected" })
        {
            if (result[name] is JsonObject node) evidence[name] = Identity(node);
        }
        if (result["matches"] is JsonArray matches)
        {
            var retained = new JsonArray();
            evidence["matches"] = retained;
            foreach (var node in matches.OfType<JsonObject>())
                if (!Append(retained, Identity(node))) break;
        }

        if (result["assertions"] is JsonArray assertions)
        {
            var retained = new JsonArray();
            evidence["assertions"] = retained;
            foreach (var assertion in assertions.OfType<JsonObject>())
            {
                var check = new JsonObject();
                foreach (var name in new[] { "assertionId", "passed", "message" }) CopyScalar(check, assertion, name);
                if (!Append(retained, check)) break;
            }
        }

        // Script return values often explain the completed work and include its measured values.
        if (result["output"] is { } taskOutput)
        {
            evidence["output"] = taskOutput.DeepClone();
            if (evidence.ToJsonString().Length > maximumCharacters - 600)
            {
                evidence.Remove("output");
                truncated = true;
                if (taskOutput is JsonObject taskObject)
                {
                    var summary = new JsonObject();
                    CopyScalar(summary, taskObject, "summary");
                    if (summary.Count > 0) evidence["output"] = summary;
                }
            }
        }

        var observation = result["afterObservation"] as JsonObject
            ?? (result["root"] is JsonObject ? result : null);
        if (observation is not null)
        {
            var snapshot = new JsonObject();
            CopyScalar(snapshot, observation, "capturedAtUtc");
            CopyScalar(snapshot, observation, "truncated");
            var nodes = new JsonArray();
            snapshot["nodes"] = nodes;
            evidence["observation"] = snapshot;
            if (observation["root"] is JsonObject root) AddNode(root);

            bool AddNode(JsonObject node)
            {
                // Preserve positive identity evidence, not stale coordinates or actionable node IDs.
                var identity = Identity(node);
                if (identity.Count > 0 && !Append(nodes, identity)) return false;
                if (node["children"] is JsonArray children)
                {
                    foreach (var child in children.OfType<JsonObject>())
                    {
                        if (!AddNode(child)) return false;
                    }
                }
                return true;
            }
        }

        // A sequential batch has one model output, but every step may supply useful evidence.
        if (result["steps"] is JsonArray steps)
        {
            var retained = new JsonArray();
            evidence["steps"] = retained;
            foreach (var step in steps.OfType<JsonObject>())
            {
                if (step["result"] is not JsonObject stepResult || Create(stepResult, 1_200) is not { } summary) continue;
                CopyScalar(summary, step, "toolName");
                CopyScalar(summary, step, "step");
                CopyScalar(summary, step, "isError");
                if (!Append(retained, summary)) break;
            }
        }

        if (evidence.Count == 0) return null;
        evidence["scope"] = "Historical evidence from this call, not current UI state. Retained observations are partial; omitted nodes do not prove absence.";
        if (truncated) evidence["summaryTruncated"] = true;
        return evidence;

        bool Append(JsonArray items, JsonObject item)
        {
            items.Add(item);
            if (evidence.ToJsonString().Length <= maximumCharacters - 600) return true;
            items.RemoveAt(items.Count - 1);
            truncated = true;
            return false;
        }
    }

    private static JsonObject Identity(JsonObject node)
    {
        var identity = new JsonObject();
        foreach (var name in new[] { "automationId", "role", "type", "text", "value", "visible", "enabled", "textTruncated", "valueTruncated" })
            CopyScalar(identity, node, name);
        return identity;
    }

    private static void CopyScalar(JsonObject target, JsonObject source, string name)
    {
        if (source[name] is not JsonValue value) return;
        if (value.TryGetValue<string>(out var text) && text.Length > MaximumTextCharacters)
        {
            target[name] = text[..MaximumTextCharacters];
            target[name + "Truncated"] = true;
        }
        else
        {
            target[name] = value.DeepClone();
        }
    }
}
