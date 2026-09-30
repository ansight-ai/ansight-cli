namespace Ansight.Host.Models.Session;



public sealed record SessionLoadProgress(
    SessionLoadStage Stage,
    string StageName,
    string StatusText,
    double StageProgress,
    double OverallProgress,
    int StageNumber,
    int TotalStageCount)
{
    public static SessionLoadProgress Complete(string statusText)
    {
        return new SessionLoadProgress(
            SessionLoadStage.Complete,
            "Complete",
            string.IsNullOrWhiteSpace(statusText) ? "Session content loaded." : statusText.Trim(),
            1,
            1,
            1,
            1);
    }
}
