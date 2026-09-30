using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionAnalysisRunResult(
    bool IsSuccess,
    string Message,
    Guid RunId)
{
    public static CloudSessionAnalysisRunResult Success(Guid runId)
        => new(true, "Cloud analysis started.", runId);

    public static CloudSessionAnalysisRunResult Failure(string message)
        => new(false, message, Guid.Empty);
}
