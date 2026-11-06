using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.NativeProfiling;

internal sealed class IosGetInstrumentsTocTool : NativeProfileOperation
{
    public IosGetInstrumentsTocTool(NativeProfileOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_ios_get_instruments_toc";

    protected override string Title => "Inspect Instruments Table of Contents";

    protected override string Description => "Inventory runs, table schemas, and processes from the retained xctrace table-of-contents XML for an iOS capture.";

    protected override JsonObject InputSchema => CaptureIdSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryReadCaptureId(arguments, out var captureId, out var error))
        {
            return Completed(error!);
        }

        try
        {
            return Completed(ToolSuccess(Serialize(Services.Profiling.InspectInstrumentsToc(captureId!))));
        }
        catch (Exception ex) when (ex is KeyNotFoundException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return Completed(ToolError(ex.Message));
        }
    }
}
