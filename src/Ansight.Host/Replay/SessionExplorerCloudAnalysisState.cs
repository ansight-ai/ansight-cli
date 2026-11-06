
namespace Ansight.Host.Replay;

public readonly record struct SessionExplorerCloudAnalysisState(
    bool IsSuccess,
    string Message,
    Guid? CloudSessionId,
    CloudSessionAnalysisCapabilities? Capabilities,
    IReadOnlyList<CloudSessionAnalysisSummary> Analyses)
{
    public static SessionExplorerCloudAnalysisState Success(
        Guid cloudSessionId,
        CloudSessionAnalysisCapabilities? capabilities,
        IReadOnlyList<CloudSessionAnalysisSummary> analyses,
        string message = "")
        => new(true, message, cloudSessionId, capabilities, analyses);

    public static SessionExplorerCloudAnalysisState Failure(string message)
        => new(false, message, null, null, Array.Empty<CloudSessionAnalysisSummary>());
}
