using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class NativeGetTraceOverviewTool : NativeProfileOperation
{
    public NativeGetTraceOverviewTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_native_get_trace_overview";

    protected override string Title => "Get Native Trace Overview";

    protected override string Description => "Summarize native capture provenance and artifacts plus Instruments table inventory or Perfetto configuration and core trace counts.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return error!;
        }

        try
        {
            return ToolSuccess(Serialize(await Services.Profiling.InspectCaptureAsync(captureId!)
                .ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is KeyNotFoundException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return ToolError(ex.Message);
        }
    }
}
