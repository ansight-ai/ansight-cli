using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal abstract class DotNetAnalysisOperation : DotNetOperation
{
    protected DotNetAnalysisOperation(DotNetOperationServices services)
        : base(services)
    {
    }

    protected bool TryAnalyze(
        JsonObject? arguments,
        out ParsedTraceAnalysisResult? result,
        out RequestResult? errorResult,
        int hotspotLimit = 100,
        int callTreeDepth = 64,
        int callTreeChildLimit = 100)
    {
        result = null;
        errorResult = null;

        if (!DotNetOperationArguments.TryReadSelection(
            arguments,
            out var captureId,
            out var selection,
            out var error))
        {
            errorResult = ToolError(error!);

            return false;
        }

        try
        {
            result = Services.Analysis.Analyze(
                captureId,
                selection,
                hotspotLimit,
                callTreeDepth,
                callTreeChildLimit);

            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            errorResult = ToolError(ex.Message);

            return false;
        }
    }

    protected static Dictionary<string, ToolSchema> SelectionProperties()
        => DotNetOperationArguments.CaptureSelectionProperties();
}
