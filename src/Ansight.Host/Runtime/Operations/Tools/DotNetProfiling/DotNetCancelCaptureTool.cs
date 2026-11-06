using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetCancelCaptureTool : DotNetOperation
{
    public DotNetCancelCaptureTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_cancel_capture";

    protected override string Title => "Cancel .NET Capture";

    protected override string Description => "Cancel an active .NET artifact preparation or EventPipe capture and terminate its owned profiler processes.";

    protected override JsonObject InputSchema => DotNetCaptureToolSchemas.CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        var captureId = arguments?["captureId"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(captureId))
        {
            return Completed(ToolError("captureId is required."));
        }

        var cancelled = Services.Profiling.CancelCapture(captureId);

        return Completed(cancelled
            ? ToolSuccess(new JsonObject
            {
                ["captureId"] = captureId,
                ["cancellationRequested"] = true
            })
            : ToolError($"Capture '{captureId}' is not active or was not found."));
    }
}
