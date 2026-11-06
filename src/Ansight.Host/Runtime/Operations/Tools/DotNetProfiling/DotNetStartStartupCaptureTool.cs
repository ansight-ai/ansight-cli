using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetStartStartupCaptureTool : DotNetOperation
{
    public DotNetStartStartupCaptureTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_start_startup_capture";

    protected override string Title => "Start .NET Startup Capture";

    protected override string Description => "Install and launch a supplied profiling-ready .app, .apk, or .ipa under the Ansight startup EventPipe capture preset.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: DotNetCaptureToolSchemas.ArtifactCaptureProperties(includeDuration: true),
        required: ["applicationPath", "appId", "deviceId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!DotNetCaptureToolSchemas.TryReadRequest(arguments, out var request, out var error))
        {
            return Completed(ToolError(error!));
        }

        try
        {
            var captureId = Services.Profiling.StartStartupCapture(request!);
            Services.Profiling.TryGetCapture(captureId, out var snapshot);

            return Completed(ToolSuccess(new JsonObject
            {
                ["captureId"] = captureId,
                ["status"] = snapshot is null ? null : DotNetOperationPayloads.Snapshot(snapshot),
                ["message"] = "The .NET startup capture has been queued. Poll ansight_dotnet_get_capture_status for completion."
            }));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            return Completed(ToolError(ex.Message));
        }
    }
}
