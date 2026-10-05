using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal abstract class AnnotationMutationTool : Operation
{
    private readonly SessionAnnotationMutationKind kind;

    protected AnnotationMutationTool(OperationServices services, SessionAnnotationMutationKind kind) : base(services)
    {
        this.kind = kind;
    }

    public override string Name => kind switch
    {
        SessionAnnotationMutationKind.Create => "ansight_create_annotation",
        SessionAnnotationMutationKind.Patch => "ansight_patch_annotation",
        _ => "ansight_remove_annotation"
    };

    protected override string Title => kind switch
    {
        SessionAnnotationMutationKind.Create => "Create Session Annotation",
        SessionAnnotationMutationKind.Patch => "Patch Session Annotation",
        _ => "Remove Session Annotation"
    };

    protected override string Description => kind switch
    {
        SessionAnnotationMutationKind.Create => "Create a timeline or screenshot annotation. An existing annotation id is an error. Screenshot and visual-tree references must belong to the selected session.",
        SessionAnnotationMutationKind.Patch => "Patch an existing annotation atomically. Omitted fields and attached evidence are preserved. Null clears notes, status, endUtc, or target; an empty geometries array clears screenshot shapes.",
        _ => "Delete an existing annotation atomically, optionally guarding its source. Returns the removed annotation."
    };

    protected override JsonObject InputSchema => AnnotationMutationToolSchema.Build(kind);

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!AnnotationMutationArgumentReader.TryRead(arguments, kind, out var mutation, out var error))
        {
            return Task.FromResult(ToolError(error!));
        }
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }
        var result = runtimeState.MutateSessionAnnotation(snapshot!.SessionId, mutation!);
        return Task.FromResult(result.IsSuccess
            ? RequestResult.ToolResult(result.Payload!, isError: false)
            : ToolError(result.Message));
    }
}
