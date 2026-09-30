using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations;

internal static class ToolResultSummary
{
    public static string Build(JsonObject payload, bool isError)
    {
        var summaryParts = new List<string>();

        if (payload["message"] is JsonValue messageValue)
        {
            var message = messageValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                summaryParts.Add(message.Trim());
            }
        }

        if (payload["toolId"] is JsonValue toolIdValue)
        {
            var toolId = toolIdValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(toolId))
            {
                summaryParts.Add($"toolId={toolId.Trim()}");
            }
        }

        if (payload["configId"] is JsonValue configIdValue)
        {
            var configId = configIdValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(configId))
            {
                summaryParts.Add($"configId={configId.Trim()}");
            }
        }

        if (payload["sessionId"] is JsonValue sessionIdValue)
        {
            var sessionId = sessionIdValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                summaryParts.Add($"sessionId={sessionId.Trim()}");
            }
        }

        if (payload["appId"] is JsonValue appIdValue)
        {
            var appId = appIdValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(appId))
            {
                summaryParts.Add($"appId={appId.Trim()}");
            }
        }

        if (payload["responseType"] is JsonValue responseTypeValue)
        {
            var responseType = responseTypeValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(responseType))
            {
                summaryParts.Add($"responseType={responseType.Trim()}");
            }
        }

        if (payload["catalog"] is JsonObject catalog
            && catalog["tools"] is JsonArray tools)
        {
            summaryParts.Add($"{tools.Count} tool(s)");
        }

        if (payload["apps"] is JsonArray apps)
        {
            summaryParts.Add($"{apps.Count} app(s)");
        }

        if (payload["configs"] is JsonArray configs)
        {
            summaryParts.Add($"{configs.Count} config(s)");
        }

        if (payload["devices"] is JsonArray devices)
        {
            summaryParts.Add($"{devices.Count} device(s)");
        }

        if (payload["sessions"] is JsonArray sessions)
        {
            summaryParts.Add($"{sessions.Count} session(s)");
        }

        if (payload["logs"] is JsonArray logs)
        {
            summaryParts.Add($"{logs.Count} log(s)");
        }

        if (payload["telemetry"] is JsonArray telemetry)
        {
            summaryParts.Add($"{telemetry.Count} telemetry channel(s)");
        }

        if (payload["matchedLogCount"] is JsonValue matchedLogCountValue)
        {
            summaryParts.Add($"{matchedLogCountValue.GetValue<int>()} matched log(s)");
        }

        if (payload["matchedSampleCount"] is JsonValue matchedSampleCountValue)
        {
            summaryParts.Add($"{matchedSampleCountValue.GetValue<int>()} matched sample(s)");
        }

        if (payload["payload"] is JsonObject responsePayload)
        {
            summaryParts.Add(BuildPayloadShapeSummary(responsePayload));
        }
        else if (payload["error"] is JsonObject errorPayload)
        {
            summaryParts.Add(BuildPayloadShapeSummary(errorPayload));
        }

        if (summaryParts.Count == 0)
        {
            summaryParts.Add(BuildPayloadShapeSummary(payload));
        }

        var prefix = isError ? "Tool call failed" : "Tool call succeeded";
        return $"{prefix}. {string.Join(" | ", summaryParts.Where(part => !string.IsNullOrWhiteSpace(part)))}";
    }

    private static string BuildPayloadShapeSummary(JsonNode? node)
    {
        return node switch
        {
            JsonObject jsonObject => $"{jsonObject.Count} top-level field(s)",
            JsonArray jsonArray => $"{jsonArray.Count} item(s)",
            JsonValue jsonValue => jsonValue.ToString(),
            null => "null payload",
            _ => "payload received"
        };
    }
}
