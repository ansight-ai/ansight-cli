using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetAnnotationsTool : Operation
{
    public GetAnnotationsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_annotations";

    protected override string Title => "Get Session Annotations";

    protected override string Description => "Return session annotations filtered by time range, point-in-time, label text, status, frame id, and geometry presence.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional inclusive range start in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional inclusive range end in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["targetUtc"] = ToolSchema.String("Optional point-in-time filter in ISO-8601 UTC; only annotations active at that instant are returned.", nullable: true, format: "date-time"),
            ["labelQuery"] = ToolSchema.String("Optional case-insensitive substring matched against the annotation label and notes.", nullable: true),
            ["annotationStatus"] = ToolSchema.String("Optional exact annotation status, case-insensitive.", nullable: true),
            ["hasStatus"] = ToolSchema.Boolean("True selects annotations with a status; false selects those without one.", nullable: true),
            ["frameId"] = ToolSchema.String("Optional screenshot frame id; only annotations with geometry on that frame are returned.", nullable: true),
            ["hasGeometry"] = ToolSchema.Boolean("Optional geometry filter. True returns only annotations with geometry; false returns only annotations without geometry.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum number of annotations to return. Defaults to 200, max 2000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(SessionAnnotationInspection.BuildGetAnnotationsResult(sessionResolver, arguments));
}
