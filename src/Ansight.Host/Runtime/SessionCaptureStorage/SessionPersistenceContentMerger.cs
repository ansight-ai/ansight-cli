namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal static class SessionPersistenceContentMerger
{

    internal static IReadOnlyList<LogEntry> BuildCompactedLogs(
        SessionPathLayout layout,
        AppSessionSnapshot snapshot)
    {
        var persistedLogs = SessionSnapshotReader.LoadLogs(layout.LogsFilePath, layout.LogSegmentsDirectoryPath);
        var totalLogCount = Math.Max(ResolveTotalLogCount(snapshot), persistedLogs.Count);
        if (persistedLogs.Count >= totalLogCount)
        {
            var completeLogs = persistedLogs.Count == totalLogCount
                ? persistedLogs
                : persistedLogs.Take(totalLogCount).ToArray();
            return EnsureTimestampOrder(completeLogs);
        }

        var retainedStartIndex = ResolveRetainedLogStartIndex(snapshot);
        var firstUnpersistedRetainedIndex = Math.Max(0, persistedLogs.Count - retainedStartIndex);
        if (firstUnpersistedRetainedIndex >= snapshot.Logs.Count)
        {
            return EnsureTimestampOrder(persistedLogs);
        }

        var compactedLogs = new List<LogEntry>(Math.Max(totalLogCount, persistedLogs.Count + snapshot.Logs.Count));
        compactedLogs.AddRange(persistedLogs);
        for (var index = firstUnpersistedRetainedIndex; index < snapshot.Logs.Count; index++)
        {
            compactedLogs.Add(snapshot.Logs[index]);
        }

        return EnsureTimestampOrder(compactedLogs);
    }

    internal static IReadOnlyList<SessionImageFrame> BuildCompactedImages(
        SessionPathLayout layout,
        AppSessionSnapshot snapshot)
    {
        var imagesById = SessionSnapshotReader.LoadImages(
                layout.ImagesFilePath,
                layout.CapturedImagesDirectoryPath)
            .Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId))
            .ToDictionary(frame => frame.FrameId, StringComparer.Ordinal);
        foreach (var frame in snapshot.Images.Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId)))
        {
            imagesById.TryAdd(frame.FrameId, frame);
        }

        return imagesById.Values
            .OrderBy(frame => frame.CapturedAtUtc)
            .ThenBy(frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<SessionTouchInputRecord> BuildCompactedTouches(
        SessionPathLayout layout,
        AppSessionSnapshot snapshot)
    {
        var touchesById = SessionSnapshotReader.LoadTouches(layout)
            .Where(touch => !string.IsNullOrWhiteSpace(touch.Id))
            .ToDictionary(touch => touch.Id, StringComparer.Ordinal);
        foreach (var touch in snapshot.Touches.Where(touch => !string.IsNullOrWhiteSpace(touch.Id)))
        {
            touchesById.TryAdd(touch.Id, touch);
        }

        return touchesById.Values
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<SessionMetricSample> BuildCompactedMetrics(
        SessionPathLayout layout,
        AppSessionSnapshot snapshot)
    {
        var persistedMetrics = SessionSnapshotReader.LoadTelemetry(layout.TelemetryDirectoryPath);
        var totalMetricSampleCount = Math.Max(
            Math.Max(snapshot.TotalMetricSampleCount, snapshot.Metrics.Count),
            persistedMetrics.Count);
        if (persistedMetrics.Count >= totalMetricSampleCount)
        {
            return persistedMetrics;
        }

        var retainedMetricStartIndex = Math.Max(0, totalMetricSampleCount - snapshot.Metrics.Count);
        var firstUnpersistedRetainedIndex = Math.Max(0, persistedMetrics.Count - retainedMetricStartIndex);
        var compactedMetrics = new List<SessionMetricSample>(totalMetricSampleCount);
        compactedMetrics.AddRange(persistedMetrics);
        for (var index = firstUnpersistedRetainedIndex; index < snapshot.Metrics.Count; index++)
        {
            compactedMetrics.Add(snapshot.Metrics[index]);
        }

        return compactedMetrics
            .DistinctBy(metric => new MetricDeduplicationKey(
                metric.ChannelId,
                metric.Value,
                metric.CapturedAtUtc))
            .OrderBy(metric => metric.CapturedAtUtc)
            .ThenBy(metric => metric.ChannelId)
            .ToArray();
    }

    internal static IReadOnlyList<LogEntry> EnsureTimestampOrder(IReadOnlyList<LogEntry> logs)
        => SessionSnapshotReader.IsTimestampOrdered(logs) ? logs : logs.OrderBy(entry => entry.TimestampUtc).ToArray();

    private static int ResolveTotalLogCount(AppSessionSnapshot snapshot)
        => Math.Max(snapshot.TotalLogCount, snapshot.Logs.Count);

    private static int ResolveRetainedLogStartIndex(AppSessionSnapshot snapshot)
    {
        var totalLogCount = ResolveTotalLogCount(snapshot);
        var inferredStartIndex = Math.Max(0, totalLogCount - snapshot.Logs.Count);
        return snapshot.RetainedLogStartIndex <= 0
            ? inferredStartIndex
            : Math.Clamp(snapshot.RetainedLogStartIndex, 0, inferredStartIndex);
    }
}
