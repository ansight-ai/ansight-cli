using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetCaptureManifestTool : DotNetOperation
{
    public DotNetGetCaptureManifestTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_capture_manifest";

    protected override string Title => "Get .NET Capture Manifest";

    protected override string Description => "Return the immutable trace inventory, application provenance, hashes, tool versions, warnings, and state for one .NET capture.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        return Services.Profiling.CaptureStore.TryLoadManifest(captureId, out var manifest) && manifest is not null
            ? Completed(ToolSuccess(DotNetOperationPayloads.Manifest(manifest)))
            : Completed(ToolError($".NET trace capture '{captureId}' was not found."));
    }
}
