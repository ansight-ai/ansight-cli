using Ansight.Host.Trends;

namespace Ansight.Host.Cloud;

public sealed record CloudTrendsUploadResult(
    bool IsSuccess,
    string Message,
    int MetricCount,
    int HistoryCount)
{
    public static CloudTrendsUploadResult Success(int metricCount, int historyCount)
        => new(
            true,
            $"Uploaded {metricCount} trends metric(s) and {historyCount} trends history result(s).",
            metricCount,
            historyCount);

    public static CloudTrendsUploadResult Failure(string message)
        => new(false, message, 0, 0);
}
