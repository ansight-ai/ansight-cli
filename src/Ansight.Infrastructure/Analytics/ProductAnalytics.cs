using Ansight.Infrastructure;

namespace Ansight.Analytics;

/// <summary>
/// Canonical product-analytics entry point for a single installation.
/// Keeps host call sites independent of the analytics storage layout.
/// </summary>
public sealed class ProductAnalytics
{
    private readonly string dataDirectory;

    public ProductAnalytics(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public static ProductAnalytics For(IApplicationPaths applicationPaths)
        => new(ApplicationPathsFactory.ResolveBaseFolderPath(applicationPaths));

    public string DataDirectoryPath => dataDirectory;

    public void RecordUsage(
        string feature,
        string surface = "host",
        string outcome = "observed",
        double durationSeconds = 0,
        bool hasEvidence = false,
        int count = 1)
        => ProductUsage.Record(
            dataDirectory,
            feature,
            surface,
            outcome,
            durationSeconds,
            hasEvidence,
            count);

    public Task<T> ObserveUsageAsync<T>(
        string feature,
        Func<Task<T>> operation,
        Func<T, string> classify)
        => ProductUsage.ObserveAsync(dataDirectory, feature, operation, classify);

    public T ObserveUsage<T>(
        string feature,
        Func<T> operation,
        Func<T, string> classify)
        => ProductUsage.Observe(dataDirectory, feature, operation, classify);

    public void TrackAcquisition(
        string eventName,
        string? attemptId = null,
        string? accountId = null,
        string? outcome = null,
        bool once = false)
        => AcquisitionAnalytics.Track(
            dataDirectory,
            eventName,
            attemptId,
            accountId,
            outcome,
            once);

    public void IdentifyAccount(string? accountId, string surface = "host")
        => AcquisitionAnalytics.IdentifyAccount(dataDirectory, accountId, surface);

    /// <summary>Records a user-initiated operation after its business has been resolved.</summary>
    public void RecordBusinessActivity(Guid businessId)
    {
        if (businessId == Guid.Empty) return;
        using var actor = AnalyticsAccount.Capture(dataDirectory, AnalyticsAccount.Read(dataDirectory), businessId.ToString("D"));
        ProductUsage.Record(dataDirectory, "business_activity", "host");
    }

    public void Flush(bool force = false)
        => ProductUsage.Flush(dataDirectory, force);
}
