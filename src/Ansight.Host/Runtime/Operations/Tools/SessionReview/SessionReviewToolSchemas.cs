using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal static class SessionReviewToolSchemas
{
    public static Dictionary<string, ToolSchema> ReviewSessionProperties()
    {
        return new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["platform"] = SessionInspectionToolSchemas.PlatformSchema("Optional platform key filter."),
            ["platforms"] = SessionInspectionToolSchemas.PlatformsSchema()
        };
    }

    public static Dictionary<string, ToolSchema> ReviewTimeRangeProperties()
    {
        var properties = ReviewSessionProperties();
        properties["startUtc"] = ToolSchema.String("Optional inclusive start timestamp in ISO-8601 UTC.", nullable: true, format: "date-time");
        properties["endUtc"] = ToolSchema.String("Optional inclusive end timestamp in ISO-8601 UTC.", nullable: true, format: "date-time");
        return properties;
    }

    public static Dictionary<string, ToolSchema> TouchFilterProperties()
    {
        var properties = ReviewTimeRangeProperties();
        properties["actions"] = ToolSchema.Array(
            ToolSchema.String("Touch action to include.", enumValues: ["down", "move", "up", "cancel", "unknown"]),
            description: "Optional touch action filters.",
            nullable: true);
        properties["pointerIds"] = ToolSchema.Array(
            ToolSchema.Integer("Pointer id to include."),
            description: "Optional pointer id filters.",
            nullable: true);
        properties["minPointerCount"] = ToolSchema.Integer("Optional minimum simultaneous pointer count.", nullable: true);
        properties["maxPointerCount"] = ToolSchema.Integer("Optional maximum simultaneous pointer count.", nullable: true);
        return properties;
    }
}
