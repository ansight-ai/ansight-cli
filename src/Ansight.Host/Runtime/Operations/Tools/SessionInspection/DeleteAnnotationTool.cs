using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class DeleteAnnotationTool : Operation
{
    public DeleteAnnotationTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_delete_annotation";

    protected override string Title => "Delete Session Annotation";

    protected override string Description => "Delete an existing session annotation, optionally guarding by its current source.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to update.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["annotationId"] = ToolSchema.String("Required annotation id to delete."),
            ["expectedSource"] = ToolSchema.String("Optional source guard; the delete is rejected if the existing annotation source differs.", nullable: true)
        },
        required: ["annotationId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        var annotationId = NormalizeOptionalString(arguments?["annotationId"]?.GetValue<string>());
        if (annotationId is null)
        {
            return Task.FromResult(ToolError("annotationId is required."));
        }

        var existingAnnotation = snapshot!.Annotations.FirstOrDefault(annotation =>
            string.Equals(annotation.AnnotationId, annotationId, StringComparison.Ordinal));
        if (existingAnnotation is null)
        {
            return Task.FromResult(ToolError($"Annotation '{annotationId}' was not found for session '{snapshot.SessionId}'."));
        }

        var expectedSource = NormalizeOptionalString(arguments?["expectedSource"]?.GetValue<string>());
        if (expectedSource is not null && !string.Equals(existingAnnotation.Source, expectedSource, StringComparison.Ordinal))
        {
            return Task.FromResult(ToolError($"Annotation '{annotationId}' source is '{existingAnnotation.Source}', not '{expectedSource}'."));
        }

        var result = runtimeState.DeleteSessionAnnotation(snapshot.SessionId, annotationId);
        if (!result.IsSuccess)
        {
            return Task.FromResult(ToolError(result.Message));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["annotationId"] = existingAnnotation.AnnotationId,
                ["source"] = existingAnnotation.Source,
                ["deletedAnnotation"] = PayloadJson.BuildSessionAnnotationPayload(existingAnnotation)
            },
            isError: false));
    }
}
