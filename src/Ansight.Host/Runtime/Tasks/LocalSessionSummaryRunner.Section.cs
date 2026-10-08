using Ansight.Host.Models.Session;

namespace Ansight.Host.Runtime.Tasks;

internal static partial class LocalSessionSummaryRunner
{
    private static readonly string sectionInstructions = summaryInstructions.Replace(
        "In this session, the tester", "In this section, the tester", StringComparison.Ordinal) + "\n" + """
        The evidence is restricted to the selected time range. Summarize only this section for a timeline annotation.
        Treat its start and end as section boundaries, not the start and end of the whole session.
        Do not infer actions before or after the supplied range. Distinguish visible outcomes from uncertain causes.
        """;

    internal static AppSessionSnapshot SelectSection(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (startUtc == default || endUtc == default || endUtc <= startUtc)
            throw new ArgumentException("Choose a section with an end time after its start time.");

        bool InRange(DateTimeOffset timestamp) => timestamp >= startUtc && timestamp <= endUtc;
        var logs = snapshot.Logs.Where(item => InRange(item.TimestampUtc)).ToArray();
        var images = snapshot.Images.Where(item => InRange(item.CapturedAtUtc)).ToArray();
        var touches = snapshot.Touches.Where(item => InRange(item.CapturedAtUtc)).ToArray();
        var networkRequests = snapshot.NetworkRequests.Where(item => InRange(item.StartedAtUtc) && InRange(item.CompletedAtUtc)).ToArray();
        var visualTrees = snapshot.VisualTreeSnapshots.Where(item => InRange(item.CapturedAtUtc)).ToArray();
        var events = snapshot.ApplicationEvents.Where(item => InRange(item.CapturedAtUtc)).ToArray();
        var metrics = snapshot.Metrics.Where(item => InRange(item.CapturedAtUtc)).ToArray();
        // An annotation spanning beyond this section may describe unrelated actions outside it.
        var annotations = snapshot.Annotations.Where(item => InRange(item.StartUtc)
            && InRange(item.EndUtc ?? item.StartUtc)).ToArray();
        if (logs.Length + images.Length + touches.Length + networkRequests.Length + visualTrees.Length
            + events.Length + metrics.Length + annotations.Length == 0)
            throw new ArgumentException("The selected section has no captured evidence to summarise.");

        var metricChannels = snapshot.MetricChannels.Where(channel => metrics.Any(item => item.ChannelId == channel.ChannelId)).ToArray();
        return new AppSessionSnapshot
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            Name = snapshot.Name,
            CreatedUtc = startUtc,
            LastUpdatedUtc = endUtc,
            ConfigId = snapshot.ConfigId,
            Status = snapshot.Status,
            IsHistorical = snapshot.IsHistorical,
            ReplaySource = snapshot.ReplaySource,
            Images = images,
            Logs = logs,
            Touches = touches,
            NetworkRequests = networkRequests,
            VisualTreeSnapshots = visualTrees,
            ApplicationEvents = events,
            Annotations = annotations,
            Metrics = metrics,
            MetricChannels = metricChannels,
            TotalLogCount = logs.Length,
            TotalImageCount = images.Length,
            TotalAnnotationCount = annotations.Length,
            TotalApplicationEventCount = events.Length,
            TotalNetworkRequestCount = networkRequests.Length,
            TotalMetricSampleCount = metrics.Length,
            TotalMetricChannelCount = metricChannels.Length
        };
    }
}
