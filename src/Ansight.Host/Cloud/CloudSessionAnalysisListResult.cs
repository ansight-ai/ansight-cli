using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public readonly record struct CloudSessionAnalysisListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<CloudSessionAnalysisSummary> Analyses)
{
    public static CloudSessionAnalysisListResult Success(IReadOnlyList<CloudSessionAnalysisSummary> analyses)
        => new(true, string.Empty, analyses);

    public static CloudSessionAnalysisListResult Failure(string message)
        => new(false, message, Array.Empty<CloudSessionAnalysisSummary>());
}
