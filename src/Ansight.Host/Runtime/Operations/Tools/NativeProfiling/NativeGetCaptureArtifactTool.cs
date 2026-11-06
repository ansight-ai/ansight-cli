using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeGetCaptureArtifactTool : NativeProfileOperation
{
    public NativeGetCaptureArtifactTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_capture_artifact";

    protected override string Title => "Resolve Native Profile Artifact";

    protected override string Description => "Resolve the absolute local path and immutable evidence metadata for a native capture artifact; defaults to the authoritative trace.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["captureId"] = ToolSchema.String("Required native profile capture identifier."),
            ["artifactKind"] = ToolSchema.String("Optional exact artifact kind; defaults to the authoritative artifact.", nullable: true)
        },
        required: ["captureId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        var artifact = Services.Profiling.GetArtifact(
            captureId!,
            arguments?["artifactKind"]?.GetValue<string>());

        return Completed(artifact is null
            ? ToolError($"Native profile capture '{captureId}' or the requested artifact was not found.")
            : ToolSuccess(Serialize(artifact)));
    }
}
