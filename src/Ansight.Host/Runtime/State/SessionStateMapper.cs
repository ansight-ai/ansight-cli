namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using static RuntimeSnapshotNormalizer;

internal static partial class SessionStateMapper
{
    private static readonly TimeSpan TrailingTimelineSilenceTolerance = TimeSpan.FromSeconds(2);

    internal static SessionState CreateState(AppSessionSnapshot snapshot)
    {
        var state = new SessionState
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            Name = snapshot.Name,
            CreatedUtc = snapshot.CreatedUtc.ToUniversalTime(),
            LastUpdatedUtc = ResolveSessionTimelineEnd(snapshot),
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            IsHistorical = true,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(snapshot.SdkVersion, snapshot.DeviceProfile, snapshot.DeviceProfileJson),
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(snapshot.AppToolCatalog),
            NextTelemetrySegmentId = ResolveNextTelemetrySegmentId(snapshot.Metrics)
        };

        state.Tags.AddRange(snapshot.Tags);
        state.Analyses.AddRange(snapshot.Analyses);
        state.Annotations.AddRange(NormalizeAnnotations(snapshot.Annotations));
        state.AgentTaskLinks.AddRange(snapshot.AgentTaskLinks.Select(
            static taskLink => SessionSnapshotCloner.CloneAgentTaskLink(taskLink)));
        state.Images.AddRange(snapshot.Images);
        state.Touches.AddRange(NormalizeTouches(snapshot.Touches));
        foreach (var touchId in state.Touches
                     .Select(touch => touch.Id)
                     .Where(touchId => !string.IsNullOrWhiteSpace(touchId)))
        {
            state.SeenTouchIds.Add(touchId);
        }

        state.NetworkRequests.AddRange(snapshot.NetworkRequests
            .Select(SessionNetworkRequestSanitizer.Normalize)
            .Where(static request => request is not null)
            .Cast<SessionNetworkRequest>()
            .OrderBy(static request => request.StartedAtUtc)
            .ThenBy(static request => request.Id, StringComparer.Ordinal));
        foreach (var requestId in state.NetworkRequests.Select(static request => request.Id))
        {
            state.SeenNetworkRequestIds.Add(requestId);
        }

        state.VisualTreeSnapshots.AddRange(NormalizeVisualTreeSnapshots(snapshot.VisualTreeSnapshots));
        state.ArtifactSnapshots.AddRange(NormalizeArtifactSnapshots(snapshot.ArtifactSnapshots));
        var normalizedLogStreams = SessionLogStreams.Normalize(snapshot.LogStreams, snapshot.Logs);
        foreach (var stream in normalizedLogStreams)
        {
            state.LogStreams[stream.StreamId] = SessionLogStreamState.FromSnapshot(stream);
            state.Logs.AddRange(stream.Entries);
            var seenEventIds = state.GetSeenEventIds(stream.StreamId);
            foreach (var eventId in stream.Entries
                         .Select(log => log.EventId)
                         .Where(eventId => !string.IsNullOrWhiteSpace(eventId)))
            {
                seenEventIds.Add(eventId!);
            }
        }

        state.TotalLogCount = Math.Max(snapshot.TotalLogCount, state.Logs.Count);

        state.ApplicationEvents.AddRange(snapshot.ApplicationEvents);
        var seenApplicationEventIds = state.GetSeenEventIds("application-events");
        foreach (var eventId in snapshot.ApplicationEvents
                     .Select(static appEvent => appEvent.EventId)
                     .Where(static eventId => !string.IsNullOrWhiteSpace(eventId)))
        {
            seenApplicationEventIds.Add(eventId);
        }

        foreach (var channel in snapshot.MetricChannels)
        {
            state.MetricChannels[channel.ChannelId] = channel;
        }

        state.AddMetrics(snapshot.Metrics);
        foreach (var metric in snapshot.Metrics)
        {
            state.SeenMetricKeys.Add(new MetricDeduplicationKey(metric.ChannelId, metric.Value, metric.CapturedAtUtc));
        }

        return state;
    }

    internal static void TouchTimelineEnd(SessionState session, DateTimeOffset candidateUtc)
    {
        var normalizedCandidateUtc = candidateUtc.ToUniversalTime();
        if (normalizedCandidateUtc > session.LastUpdatedUtc.ToUniversalTime())
        {
            session.LastUpdatedUtc = normalizedCandidateUtc;
        }
    }

    internal static DateTimeOffset ResolveSessionTimelineEnd(AppSessionSnapshot snapshot)
    {
        var createdUtc = snapshot.CreatedUtc.ToUniversalTime();
        var capturedEndUtc = SessionCaptureEndResolver.TryResolve(snapshot.CaptureSource,
            snapshot.IsHistorical, snapshot.CustomProperties, createdUtc);
        if (capturedEndUtc.HasValue)
            return capturedEndUtc.Value;

        var declaredEndUtc = snapshot.LastUpdatedUtc.ToUniversalTime();
        if (declaredEndUtc < createdUtc)
        {
            declaredEndUtc = createdUtc;
        }

        if (!snapshot.IsHistorical && IsSessionStatusLive(snapshot.Status))
        {
            return declaredEndUtc;
        }

        var contentEndUtc = ResolveCapturedContentEndUtc(snapshot, createdUtc);
        if (!contentEndUtc.HasValue || contentEndUtc.Value < createdUtc)
        {
            return declaredEndUtc;
        }

        if (contentEndUtc.Value > declaredEndUtc)
        {
            return contentEndUtc.Value;
        }

        var trailingGap = declaredEndUtc - contentEndUtc.Value;
        if (trailingGap <= TrailingTimelineSilenceTolerance)
        {
            return declaredEndUtc;
        }

        var capturedDuration = contentEndUtc.Value - createdUtc;
        return trailingGap > capturedDuration
            ? contentEndUtc.Value
            : declaredEndUtc;
    }

    private static DateTimeOffset? ResolveCapturedContentEndUtc(AppSessionSnapshot snapshot, DateTimeOffset createdUtc)
    {
        DateTimeOffset? latestUtc = null;
        AddCapturedTimestamp(snapshot.AppStateChangedUtc, createdUtc, ref latestUtc);
        foreach (var log in snapshot.Logs)
        {
            AddCapturedTimestamp(log.TimestampUtc, createdUtc, ref latestUtc);
        }

        foreach (var appEvent in snapshot.ApplicationEvents)
        {
            AddCapturedTimestamp(appEvent.CapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var frame in snapshot.Images)
        {
            AddCapturedTimestamp(frame.CapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var touch in snapshot.Touches)
        {
            AddCapturedTimestamp(touch.CapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var request in snapshot.NetworkRequests)
        {
            AddCapturedTimestamp(request.CompletedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var sample in snapshot.Metrics)
        {
            AddCapturedTimestamp(sample.CapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var snapshotRecord in snapshot.VisualTreeSnapshots)
        {
            AddCapturedTimestamp(snapshotRecord.CapturedAtUtc, createdUtc, ref latestUtc);
            AddCapturedTimestamp(snapshotRecord.ScreenshotCapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var snapshotRecord in snapshot.ArtifactSnapshots)
        {
            AddCapturedTimestamp(snapshotRecord.CapturedAtUtc, createdUtc, ref latestUtc);
        }

        foreach (var annotation in snapshot.Annotations)
        {
            AddCapturedTimestamp(annotation.StartUtc, createdUtc, ref latestUtc);
            AddCapturedTimestamp(annotation.EndUtc, createdUtc, ref latestUtc);
            foreach (var geometry in annotation.Geometry)
            {
                AddCapturedTimestamp(geometry.CapturedAtUtc, createdUtc, ref latestUtc);
            }
        }

        return latestUtc;
    }

    private static void AddCapturedTimestamp(DateTimeOffset? timestampUtc, DateTimeOffset createdUtc, ref DateTimeOffset? latestUtc)
    {
        if (!timestampUtc.HasValue)
        {
            return;
        }

        AddCapturedTimestamp(timestampUtc.Value, createdUtc, ref latestUtc);
    }

    private static void AddCapturedTimestamp(DateTimeOffset timestampUtc, DateTimeOffset createdUtc, ref DateTimeOffset? latestUtc)
    {
        var normalizedTimestampUtc = timestampUtc.ToUniversalTime();
        if (normalizedTimestampUtc < createdUtc)
        {
            return;
        }

        if (!latestUtc.HasValue || normalizedTimestampUtc > latestUtc.Value)
        {
            latestUtc = normalizedTimestampUtc;
        }
    }

    private static int ResolveNextTelemetrySegmentId(IReadOnlyList<SessionMetricSample> metrics)
    {
        if (metrics.Count == 0)
        {
            return 1;
        }

        var highestSegmentId = metrics.Max(metric => metric.SegmentId);
        return Math.Max(1, highestSegmentId + 1);
    }

    private static bool IsSessionStatusLive(string? status)
    {
        var normalizedStatus = status ?? string.Empty;
        return !normalizedStatus.Contains("complete", StringComparison.OrdinalIgnoreCase)
               && !normalizedStatus.Contains("error", StringComparison.OrdinalIgnoreCase)
               && !normalizedStatus.Contains("closed", StringComparison.OrdinalIgnoreCase)
               && !normalizedStatus.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               && !normalizedStatus.Contains("sign in required", StringComparison.OrdinalIgnoreCase)
               && !normalizedStatus.Contains("rejected", StringComparison.OrdinalIgnoreCase);
    }
}

internal static partial class SessionStateMapper
{
    internal static AppSessionSnapshot CreateSnapshot(SessionState state)
    {
        return CreateSnapshot(state, SessionSnapshotContent.Full);
    }

    internal static AppSessionSnapshot CreateSnapshot(SessionState state, SessionSnapshotContent content)
    {
        var includeSessionContent = content is SessionSnapshotContent.LiveContent
            or SessionSnapshotContent.Replay
            or SessionSnapshotContent.Full;
        var includeLogs = content is SessionSnapshotContent.Replay or SessionSnapshotContent.Full;
        var snapshotLogs = !includeLogs
            ? Array.Empty<LogEntry>()
            : content == SessionSnapshotContent.Replay
                ? state.GetLiveReplaySnapshotLogs()
                : state.GetSnapshotLogs();
        var snapshotLogStreams = !includeLogs
            ? state.GetSnapshotLogStreamDescriptors()
            : content == SessionSnapshotContent.Replay
                ? state.GetLiveReplaySnapshotLogStreams()
                : state.GetSnapshotLogStreams();
        return new AppSessionSnapshot
        {
            SessionId = state.SessionId,
            AppId = state.AppId,
            ClientName = state.ClientName,
            RemoteAddress = state.RemoteAddress,
            Name = state.Name,
            CreatedUtc = state.CreatedUtc,
            ConfigId = state.ConfigId,
            ProcessSessionId = state.ProcessSessionId,
            Status = state.Status,
            LastUpdatedUtc = state.LastUpdatedUtc,
            IsHistorical = state.IsHistorical,
            CacheSizeBytes = state.CacheSizeBytes,
            IsPinned = state.IsPinned,
            Author = state.Author,
            ReplaySource = state.ReplaySource,
            CaptureSource = state.CaptureSource,
            SdkVersion = state.SdkVersion,
            Tags = state.GetSnapshotTags(),
            Notes = state.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(state.CustomProperties),
            AppState = state.AppState,
            AppStateChangedUtc = state.AppStateChangedUtc,
            DeviceProfile = state.DeviceProfile,
            DeviceProfileJson = state.DeviceProfileJson,
            AppIcon = state.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(state.AppToolCatalog),
            Analyses = state.GetSnapshotAnalyses(),
            Annotations = state.GetSnapshotAnnotations(),
            AgentTaskLinks = state.GetSnapshotAgentTaskLinks(),
            Images = state.GetSnapshotImages(),
            Touches = includeSessionContent ? state.GetSnapshotTouches() : Array.Empty<SessionTouchInputRecord>(),
            NetworkRequests = includeSessionContent ? state.GetSnapshotNetworkRequests() : Array.Empty<SessionNetworkRequest>(),
            VisualTreeSnapshots = includeSessionContent ? state.GetSnapshotVisualTrees() : Array.Empty<SessionVisualTreeSnapshot>(),
            ArtifactSnapshots = includeSessionContent ? state.GetSnapshotArtifacts() : Array.Empty<SessionArtifactSnapshot>(),
            ApplicationEvents = includeSessionContent ? state.GetSnapshotApplicationEvents() : Array.Empty<SessionApplicationEvent>(),
            Logs = snapshotLogs,
            TotalLogCount = state.TotalLogCount,
            RetainedLogStartIndex = Math.Max(
                0,
                state.TotalLogCount - (content == SessionSnapshotContent.Replay ? snapshotLogs.Count : state.Logs.Count)),
            LogStreams = snapshotLogStreams,
            MetricChannels = includeSessionContent ? state.GetSnapshotMetricChannels() : Array.Empty<SessionMetricChannel>(),
            Metrics = content switch
            {
                SessionSnapshotContent.LiveContent => state.GetLiveSnapshotMetrics(),
                SessionSnapshotContent.Replay => state.GetLiveReplaySnapshotMetrics(),
                SessionSnapshotContent.Full => state.GetSnapshotMetrics(),
                _ => Array.Empty<SessionMetricSample>()
            },
            TotalAnnotationCount = state.Annotations.Count,
            TotalImageCount = state.Images.Count,
            TotalMetricChannelCount = state.MetricChannels.Count,
            TotalMetricSampleCount = state.Metrics.Count,
            TotalApplicationEventCount = state.ApplicationEvents.Count,
            TotalNetworkRequestCount = state.NetworkRequests.Count
        };
    }

}
