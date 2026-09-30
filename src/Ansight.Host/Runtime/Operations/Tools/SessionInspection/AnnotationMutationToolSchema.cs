using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class AnnotationMutationToolSchema
{
    public static JsonObject Build(SessionAnnotationMutationKind kind)
    {
        var isCreate = kind == SessionAnnotationMutationKind.Create;
        var isPatch = kind == SessionAnnotationMutationKind.Patch;
        var properties = new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to annotate.", nullable: true),
            ["appId"] = ToolSchema.String("App id when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("Include historical sessions when resolving by appId. Defaults to true.", nullable: true),
            ["annotationId"] = ToolSchema.String(isCreate ? "Optional new id. An existing id is rejected." : "Existing annotation id.")
        };
        if (!isCreate)
        {
            properties["expectedSource"] = ToolSchema.String("Optional exact source guard checked atomically with the mutation.");
        }
        if (isCreate || isPatch)
        {
            properties["label"] = ToolSchema.String(isCreate ? "Required non-empty annotation label." : "Replacement non-empty label; omission preserves it.");
            properties["notes"] = ToolSchema.String("Annotation notes. On patch, null clears the notes.", nullable: isPatch);
            properties["startUtc"] = ToolSchema.String("ISO-8601 start timestamp with timezone. Creation defaults to the latest captured session timestamp.", format: "date-time");
            properties["endUtc"] = ToolSchema.String("ISO-8601 end timestamp with timezone. Must not precede startUtc. On patch, null clears the end.", nullable: isPatch, format: "date-time");
            properties["geometries"] = ToolSchema.Array(Geometry(), description: "Screenshot shapes; on patch, replaces the array. [] clears it.");
            properties["target"] = ToolSchema.Object(
                properties: new Dictionary<string, ToolSchema>
                {
                    ["visualTreeSnapshotId"] = ToolSchema.String("Existing snapshot id from this session."),
                    ["nodeId"] = ToolSchema.String("Exact node id within that snapshot.")
                },
                required: ["visualTreeSnapshotId", "nodeId"],
                additionalProperties: false,
                nullable: isPatch);
        }
        if (isCreate)
        {
            properties["source"] = ToolSchema.String("Annotation source. Tasks default to task:<taskId>; standalone host calls default to ansight-operation.");
        }
        return ToolSchema.Object(properties: properties, required: isCreate ? ["label"] : ["annotationId"], additionalProperties: false).ToJson();
    }

    private static ToolSchema Geometry()
        => ToolSchema.Object(
            properties: new Dictionary<string, ToolSchema>
            {
                ["geometryId"] = ToolSchema.String("Optional unique geometry id; generated when omitted."),
                ["frameId"] = ToolSchema.String("Existing screenshot frame id in the selected session."),
                ["kind"] = ToolSchema.String("Shape kind.", enumValues: ["point", "rectangle", "ellipse", "freeDraw"]),
                ["x"] = ToolSchema.Number("Normalized x in [0,1]. Required except for freeDraw."),
                ["y"] = ToolSchema.Number("Normalized y in [0,1]. Required except for freeDraw."),
                ["width"] = ToolSchema.Number("Positive normalized width for rectangle or ellipse; box must fit inside the screenshot."),
                ["height"] = ToolSchema.Number("Positive normalized height for rectangle or ellipse; box must fit inside the screenshot."),
                ["points"] = ToolSchema.Array(ToolSchema.Object(
                    properties: new Dictionary<string, ToolSchema>
                    {
                        ["x"] = ToolSchema.Number("Normalized x in [0,1]."),
                        ["y"] = ToolSchema.Number("Normalized y in [0,1].")
                    },
                    required: ["x", "y"],
                    additionalProperties: false), description: "At least two distinct path positions for freeDraw. Origin and bounds are derived."),
                ["text"] = ToolSchema.String("Optional shape-specific note."),
                ["strokeColor"] = ToolSchema.String("Optional #RRGGBB or #AARRGGBB hexadecimal color."),
                ["strokeWidth"] = ToolSchema.Number("Optional positive finite stroke width.")
            },
            required: ["frameId", "kind"],
            additionalProperties: false);
}
