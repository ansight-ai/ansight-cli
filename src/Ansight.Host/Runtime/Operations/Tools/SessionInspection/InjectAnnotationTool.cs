using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class InjectAnnotationTool : Operation
{
    public InjectAnnotationTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_inject_annotation";

    protected override string Title => "Inject Session Annotation";

    protected override string Description => "Create or update a session annotation from an agent, recording the annotation source for downstream review.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to annotate.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["annotationId"] = ToolSchema.String("Optional annotation id. A generated id is used when omitted.", nullable: true),
            ["source"] = ToolSchema.String("Annotation source. Defaults to ansight-operation.", nullable: true),
            ["label"] = ToolSchema.String("Required annotation label."),
            ["notes"] = ToolSchema.String("Optional annotation notes.", nullable: true),
            ["status"] = ToolSchema.String("Optional free-text annotation status, such as resolved.", nullable: true),
            ["startUtc"] = ToolSchema.String("Optional annotation start timestamp. Defaults to the session's latest timestamp.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional annotation end timestamp for a timespan annotation.", nullable: true, format: "date-time"),
            ["geometries"] = ToolSchema.Array(
                SessionInspectionToolSchemas.AnnotationGeometrySchema(),
                description: "Optional screenshot geometry entries.",
                nullable: true),
            ["target"] = ToolSchema.Object(
                properties: SessionInspectionToolSchemas.AnnotationTargetSchema().Properties,
                additionalProperties: false,
                nullable: true)
        },
        required: ["label"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(SessionAnnotationInspection.BuildInjectAnnotationResult(runtimeState, sessionResolver, arguments));
}
