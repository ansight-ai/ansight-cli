namespace Ansight.Host.Sessions;

internal static class SessionLiveUpdateBuilder
{
    internal static SessionLiveUpdate Create(SessionState session, SessionLiveUpdateCursor cursor)
    {
        var retainedLogStartIndex = Math.Max(0, session.TotalLogCount - session.Logs.Count);
        var requiresReset = cursor.LogIndex < retainedLogStartIndex
                            || cursor.LogIndex > session.TotalLogCount
                            || cursor.ImageIndex < 0
                            || cursor.ImageIndex > session.Images.Count
                            || cursor.TouchIndex < 0
                            || cursor.TouchIndex > session.Touches.Count
                            || cursor.NetworkRequestIndex < 0
                            || cursor.NetworkRequestIndex > session.NetworkRequests.Count
                            || cursor.VisualTreeIndex < 0
                            || cursor.VisualTreeIndex > session.VisualTreeSnapshots.Count
                            || cursor.ArtifactIndex < 0
                            || cursor.ArtifactIndex > session.ArtifactSnapshots.Count
                            || cursor.MetricIndex < 0
                            || cursor.MetricIndex > session.Metrics.Count;

        var logs = requiresReset
            ? Array.Empty<LogEntry>()
            : CopyFrom(session.Logs, cursor.LogIndex - retainedLogStartIndex);
        var logStreams = session.GetSnapshotLogStreamDescriptors()
            .Select(stream => new SessionLogStream
            {
                StreamId = stream.StreamId,
                Kind = stream.Kind,
                DisplayName = stream.DisplayName,
                Status = stream.Status,
                StartedUtc = stream.StartedUtc,
                EndedUtc = stream.EndedUtc,
                StatusMessage = stream.StatusMessage,
                Metadata = stream.Metadata,
                Entries = logs.Where(log => string.Equals(log.StreamId, stream.StreamId, StringComparison.Ordinal)).ToArray(),
                TotalEntryCount = stream.TotalEntryCount,
                RetainedEntryStartIndex = stream.RetainedEntryStartIndex
            })
            .ToArray();

        return new SessionLiveUpdate(
            session.SessionId,
            requiresReset,
            session.LastUpdatedUtc,
            session.Status,
            session.AppState,
            session.AppStateChangedUtc,
            logs,
            logStreams,
            requiresReset ? Array.Empty<SessionImageFrame>() : CopyFrom(session.Images, cursor.ImageIndex),
            requiresReset ? Array.Empty<SessionTouchInputRecord>() : CopyFrom(session.Touches, cursor.TouchIndex),
            requiresReset ? Array.Empty<SessionNetworkRequest>() : CopyFrom(session.GetSnapshotNetworkRequests(), cursor.NetworkRequestIndex),
            requiresReset ? Array.Empty<SessionVisualTreeSnapshot>() : CopyFrom(session.GetSnapshotVisualTrees(), cursor.VisualTreeIndex),
            requiresReset ? Array.Empty<SessionArtifactSnapshot>() : CopyFrom(session.GetSnapshotArtifacts(), cursor.ArtifactIndex),
            cursor.MetricChannelCount == session.MetricChannels.Count
                ? Array.Empty<SessionMetricChannel>()
                : session.GetSnapshotMetricChannels(),
            requiresReset ? Array.Empty<SessionMetricSample>() : CopyFrom(session.Metrics, cursor.MetricIndex),
            cursor.AnnotationCount == session.Annotations.Count
                ? Array.Empty<SessionAnnotation>()
                : session.GetSnapshotAnnotations(),
            cursor.AnalysisCount == session.Analyses.Count
                ? Array.Empty<SessionAnalysisRecord>()
                : session.GetSnapshotAnalyses(),
            session.TotalLogCount,
            session.Images.Count,
            session.Touches.Count,
            session.NetworkRequests.Count,
            session.VisualTreeSnapshots.Count,
            session.ArtifactSnapshots.Count,
            session.MetricChannels.Count,
            session.Metrics.Count,
            session.Annotations.Count,
            session.Analyses.Count)
        {
            CustomProperties = session.CustomProperties?.DeepClone().AsObject(),
            LifecycleEvents = session.ApplicationEvents.Where(appEvent =>
                appEvent.Label is "lifecycle.foreground" or "lifecycle.background").ToArray()
        };
    }

    private static IReadOnlyList<T> CopyFrom<T>(IReadOnlyList<T> source, int startIndex)
    {
        if (startIndex >= source.Count)
        {
            return Array.Empty<T>();
        }

        var result = new T[source.Count - startIndex];
        for (var index = startIndex; index < source.Count; index++)
        {
            result[index - startIndex] = source[index];
        }

        return result;
    }
}
