using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionInspectionToolSchemas
{
    public static JsonObject SessionSelectorSchema()
    {
        return ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
                ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
                ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true)
            },
            additionalProperties: false).ToJson();
    }

    public static Dictionary<string, ToolSchema> LogReviewFilterProperties(bool includeRequiredQuery)
    {
        var queryDescription = includeRequiredQuery
            ? "Required case-insensitive keyword or phrase to match against log message, tag, source, or event id."
            : "Optional case-insensitive keyword or phrase to match against message, tag, source, or event id.";
        return new Dictionary<string, ToolSchema>
        {
            ["query"] = ToolSchema.String(queryDescription, nullable: !includeRequiredQuery),
            ["sessionId"] = ToolSchema.String("Optional specific session id to search.", nullable: true),
            ["appId"] = ToolSchema.String("Optional app id to filter sessions.", nullable: true),
            ["platform"] = PlatformSchema("Optional platform key filter."),
            ["platforms"] = PlatformsSchema(),
            ["includeHistorical"] = ToolSchema.Boolean("Include historical sessions. Defaults to true.", nullable: true),
            ["liveOnly"] = ToolSchema.Boolean("Restrict search to currently connected live sessions.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional inclusive start timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional inclusive end timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["minimumVerbosity"] = MinimumVerbositySchema(),
            ["streamIds"] = StreamIdsSchema(),
            ["tags"] = TagsSchema(),
            ["sources"] = SourcesSchema()
        };
    }

    public static ToolSchema MinimumVerbositySchema()
    {
        return ToolSchema.String(
            "Optional minimum log level to include.",
            enumValues: ["verbose", "debug", "information", "warning", "error", "fatal"],
            nullable: true);
    }

    public static ToolSchema TagsSchema()
    {
        return ToolSchema.Array(
            ToolSchema.String("Tag to match exactly, case-insensitive."),
            description: "Optional tag filters. Any matching tag is included.",
            nullable: true);
    }

    public static ToolSchema StreamIdsSchema()
    {
        return ToolSchema.Array(
            ToolSchema.String("Log stream id, such as sdk, android-logcat, or apple-unified-log."),
            description: "Optional log stream filters. Any matching stream is included.",
            nullable: true);
    }

    public static ToolSchema SourcesSchema()
    {
        return ToolSchema.Array(
            ToolSchema.String("Source to match exactly, case-insensitive."),
            description: "Optional source filters. Any matching source is included.",
            nullable: true);
    }

    public static ToolSchema PlatformSchema(string description)
    {
        return ToolSchema.String(
            description,
            enumValues: ["android", "ios", "macos", "apple", "windows", "other"],
            nullable: true);
    }

    public static ToolSchema PlatformsSchema()
    {
        return ToolSchema.Array(
            ToolSchema.String(
                "Platform key.",
                enumValues: ["android", "ios", "macos", "apple", "windows", "other"]),
            description: "Optional platform key filters.",
            nullable: true);
    }

    public static ToolSchema DeviceFormFactorSchema(string description)
    {
        return ToolSchema.String(
            description,
            enumValues: ["phone", "tablet", "desktop", "tv", "watch", "car", "vr", "unknown"],
            nullable: true);
    }

    public static ToolSchema DeviceFormFactorsSchema(string description)
    {
        return ToolSchema.Array(
            ToolSchema.String(
                "Device form factor.",
                enumValues: ["phone", "tablet", "desktop", "tv", "watch", "car", "vr", "unknown"]),
            description: description,
            nullable: true);
    }

    public static ToolSchema DeviceTypeSchema(string description)
    {
        return ToolSchema.String(
            description,
            enumValues: ["physical", "virtual", "emulator", "simulator", "sim"],
            nullable: true);
    }

    public static ToolSchema DeviceTypesSchema(string description)
    {
        return ToolSchema.Array(
            ToolSchema.String(
                "Device type.",
                enumValues: ["physical", "virtual", "emulator", "simulator", "sim"]),
            description: description,
            nullable: true);
    }

    public static ToolSchema AnnotationGeometrySchema()
    {
        return ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["geometryId"] = ToolSchema.String("Optional geometry id. A generated id is used when omitted.", nullable: true),
                ["frameId"] = ToolSchema.String("Screenshot frame id that the geometry belongs to."),
                ["capturedAtUtc"] = ToolSchema.String("Optional geometry capture timestamp. Defaults to the annotation start time.", nullable: true, format: "date-time"),
                ["kind"] = ToolSchema.String(
                    "Geometry kind. Defaults to rectangle when width and height are supplied, otherwise point.",
                    enumValues: ["point", "rectangle", "ellipse", "freeDraw"],
                    nullable: true),
                ["x"] = ToolSchema.Number("Normalized horizontal origin, 0.0 to 1.0."),
                ["y"] = ToolSchema.Number("Normalized vertical origin, 0.0 to 1.0."),
                ["width"] = ToolSchema.Number("Normalized width, required for rectangle and ellipse geometry.", nullable: true),
                ["height"] = ToolSchema.Number("Normalized height, required for rectangle and ellipse geometry.", nullable: true),
                ["points"] = ToolSchema.Array(
                    ToolSchema.Object(
                        properties: new Dictionary<string, ToolSchema>
                        {
                            ["x"] = ToolSchema.Number("Normalized horizontal coordinate, 0.0 to 1.0."),
                            ["y"] = ToolSchema.Number("Normalized vertical coordinate, 0.0 to 1.0.")
                        },
                        required: ["x", "y"],
                        additionalProperties: false),
                    description: "Normalized path points, required for freeDraw geometry.",
                    nullable: true),
                ["text"] = ToolSchema.String("Optional text associated specifically with this geometry.", nullable: true),
                ["strokeColor"] = ToolSchema.String("Optional geometry stroke color.", nullable: true),
                ["strokeWidth"] = ToolSchema.Number("Optional geometry stroke width.", nullable: true)
            },
            required: ["frameId", "x", "y"],
            additionalProperties: false);
    }

    public static ToolSchema AnnotationTargetSchema()
    {
        var annotationBoundsSchema = ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["x"] = ToolSchema.Number("Horizontal origin."),
                ["y"] = ToolSchema.Number("Vertical origin."),
                ["width"] = ToolSchema.Number("Bounds width."),
                ["height"] = ToolSchema.Number("Bounds height.")
            },
            required: ["x", "y", "width", "height"],
            additionalProperties: false);
        return ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["kind"] = ToolSchema.String("Target kind, for example visualTreeElement or visualTreeRegion.", nullable: true),
                ["source"] = ToolSchema.String("Target source. Defaults to the annotation source when omitted.", nullable: true),
                ["targetId"] = ToolSchema.String("Target identifier. A generated id is used when omitted.", nullable: true),
                ["visualTreeSnapshotId"] = ToolSchema.String("Associated visual tree snapshot id.", nullable: true),
                ["type"] = ToolSchema.String("Target type display value.", nullable: true),
                ["elementKind"] = ToolSchema.String("Target element kind display value.", nullable: true),
                ["label"] = ToolSchema.String("Target label display value.", nullable: true),
                ["automationId"] = ToolSchema.String("Target automation id.", nullable: true),
                ["depth"] = ToolSchema.Integer("Target depth.", nullable: true),
                ["childCount"] = ToolSchema.Integer("Target child count.", nullable: true),
                ["absoluteBounds"] = ToolSchema.Object(
                    properties: annotationBoundsSchema.Properties,
                    required: annotationBoundsSchema.Required,
                    additionalProperties: false,
                    nullable: true),
                ["normalizedBounds"] = ToolSchema.Object(
                    properties: annotationBoundsSchema.Properties,
                    required: annotationBoundsSchema.Required,
                    additionalProperties: false,
                    nullable: true)
            },
            additionalProperties: false);
    }
}
