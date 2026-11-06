using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeGetCaptureManifestTool : NativeProfileOperation
{
    public NativeGetCaptureManifestTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_capture_manifest";

    protected override string Title => "Get Native Profile Manifest";

    protected override string Description => "Return the retained native capture manifest, artifact hashes, tool versions, warnings, state, and application provenance.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        var manifest = Services.Profiling.GetManifest(captureId!);

        return Completed(manifest is null
            ? ToolError($"Native profile capture '{captureId}' was not found.")
            : ToolSuccess(Serialize(manifest)));
    }
}
