using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class NetworkInspectionToolSchemas
{
    public static JsonObject Query()
    {
        var properties = SessionProperties();
        properties["startUtc"] = ToolSchema.String("Inclusive ISO-8601 request start time.", format: "date-time");
        properties["endUtc"] = ToolSchema.String("Inclusive ISO-8601 request start time upper bound.", format: "date-time");
        properties["methods"] = ToolSchema.Array(ToolSchema.String("Case-insensitive HTTP method."));
        properties["host"] = ToolSchema.String("Case-insensitive hostname substring.");
        properties["query"] = ToolSchema.String("Case-insensitive URL, method, or error message substring.");
        properties["failedOnly"] = ToolSchema.Boolean("Return transport errors and HTTP status 400 or greater.");
        properties["limit"] = ToolSchema.Integer("Page size, default 200, range 1 to 1000.");
        properties["cursor"] = ToolSchema.String("Continue a stable network snapshot. Cursors expire after five minutes or cache eviction.");
        var schema = ToolSchema.Object(properties: properties, additionalProperties: false).ToJson();
        schema["properties"]!["statuses"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "HTTP codes or classes, combined with OR.",
            ["items"] = new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    new JsonObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 599 },
                    new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("1xx", "2xx", "3xx", "4xx", "5xx") }
                }
            }
        };
        return schema;
    }

    public static JsonObject Request(bool body = false)
    {
        var properties = SessionProperties();
        properties["requestId"] = ToolSchema.String("Exact retained request identifier in this session.");
        if (body)
        {
            properties["side"] = ToolSchema.String("Body side: request or response.");
            properties["maxBytes"] = ToolSchema.Integer("Maximum decoded preview bytes, default 16384, range 1 to 65536.");
        }

        var schema = ToolSchema.Object(properties: properties, required: body ? ["requestId", "side"] : ["requestId"], additionalProperties: false).ToJson();
        if (body)
        {
            schema["properties"]!["side"]!["enum"] = new JsonArray("request", "response");
        }

        return schema;
    }

    private static Dictionary<string, ToolSchema> SessionProperties() => new()
    {
        ["sessionId"] = ToolSchema.String("Session to inspect.", nullable: true),
        ["appId"] = ToolSchema.String("Resolve the only captured session for this app.", nullable: true),
        ["includeHistorical"] = ToolSchema.Boolean("Include historical sessions when resolving by app, default true.", nullable: true)
    };
}
