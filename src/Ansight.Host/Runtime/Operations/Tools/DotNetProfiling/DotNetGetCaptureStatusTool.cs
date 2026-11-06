using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetCaptureStatusTool : DotNetOperation
{
    public DotNetGetCaptureStatusTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_capture_status";

    protected override string Title => "Get .NET Capture Status";

    protected override string Description => "Return current artifact preparation, capture, derivation, completion, cancellation, or failure state for a .NET capture job.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        return Services.Profiling.TryGetCapture(captureId, out var snapshot) && snapshot is not null
            ? Completed(ToolSuccess(DotNetOperationPayloads.Snapshot(snapshot)))
            : Completed(ToolError($".NET trace capture '{captureId}' was not found."));
    }
}
