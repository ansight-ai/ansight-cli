namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using System.Text.Json.Nodes;
using Ansight.Pairing.Models;

internal sealed class SessionState
{
    private const int MaximumSeenMetricKeyCount = 65_536;
    private const int MaximumLiveMetricSnapshotCount = 50_000;
    private const int MaximumLiveReplayMetricSnapshotCount = 40_000;
    private const int MaximumLiveReplayLogSnapshotCount = 10_000;
    private const int MaximumSeenTouchIdCount = 65_536;
    private const int MaximumSeenNetworkRequestIdCount = 65_536;

    private static readonly TimeSpan LiveMetricSnapshotWindow = TimeSpan.FromMinutes(2);

    public required string SessionId { get; init; }
    public required string AppId { get; init; }
    public required string ClientName { get; set; }
    public required string RemoteAddress { get; set; }
    public string? Name { get; set; }
    public required DateTimeOffset CreatedUtc { get; set; }
    public required DateTimeOffset LastUpdatedUtc { get; set; }
    public required string Status { get; set; }
    public required string? ConfigId { get; set; }
    public string? ProcessSessionId { get; set; }
    public bool IsHistorical { get; set; }
    public long CacheSizeBytes { get; set; }
    public bool IsPinned { get; set; }
    public SessionCaptureAuthorMetadata? Author { get; set; }
    public SessionReplaySource? ReplaySource { get; set; }
    public string CaptureSource { get; set; } = WorkspaceExecutionModes.Sdk;
    public string? SdkVersion { get; set; }
    public string? Notes { get; set; }
    public JsonObject? CustomProperties { get; set; }
    public AppLifecycleState AppState { get; set; } = AppLifecycleState.Unknown;
    public DateTimeOffset? AppStateChangedUtc { get; set; }
    public DeviceAppProfile? DeviceProfile { get; set; }
    public string? DeviceProfileJson { get; set; }
    public SessionAppIcon? AppIcon { get; set; }
    public SessionAppToolCatalogSnapshot? AppToolCatalog { get; set; }
    public int NextTelemetrySegmentId { get; set; } = 1;
    public List<string> Tags { get; } = [];
    public List<SessionAnalysisRecord> Analyses { get; } = [];
    public List<SessionAnnotation> Annotations { get; } = [];
    public List<SessionAgentTaskLink> AgentTaskLinks { get; } = [];
    public List<SessionImageFrame> Images { get; } = [];
    public List<SessionTouchInputRecord> Touches { get; } = [];
    public List<SessionNetworkRequest> NetworkRequests { get; } = [];
    public List<SessionVisualTreeSnapshot> VisualTreeSnapshots { get; } = [];
    public List<SessionArtifactSnapshot> ArtifactSnapshots { get; } = [];
    public List<SessionApplicationEvent> ApplicationEvents { get; } = [];
    public List<LogEntry> Logs { get; } = [];
    public int TotalLogCount { get; set; }
    public Dictionary<string, SessionLogStreamState> LogStreams { get; } = new(StringComparer.Ordinal);
    public Dictionary<byte, SessionMetricChannel> MetricChannels { get; } = new();
    public List<SessionMetricSample> Metrics { get; } = [];
    public Dictionary<string, BoundedDeduplicationSet<string>> SeenEventIdsByStream { get; } = new(StringComparer.Ordinal);
    public BoundedDeduplicationSet<MetricDeduplicationKey> SeenMetricKeys { get; } = new(MaximumSeenMetricKeyCount);
    public BoundedDeduplicationSet<string> SeenTouchIds { get; } = new(MaximumSeenTouchIdCount, StringComparer.Ordinal);
    public BoundedDeduplicationSet<string> SeenNetworkRequestIds { get; } = new(MaximumSeenNetworkRequestIdCount, StringComparer.Ordinal);

    private long tagsVersion;
    private long analysesVersion;
    private long annotationsVersion;
    private long agentTaskLinksVersion;
    private long imagesVersion;
    private long touchesVersion;
    private long networkRequestsVersion;
    private long visualTreesVersion;
    private long artifactsVersion;
    private long applicationEventsVersion;
    private long logsVersion;
    private long metricChannelsVersion;
    private long metricsVersion;
    private long cachedTagsVersion = -1;
    private long cachedAnalysesVersion = -1;
    private long cachedAnnotationsVersion = -1;
    private long cachedAgentTaskLinksVersion = -1;
    private long cachedImagesVersion = -1;
    private long cachedTouchesVersion = -1;
    private long cachedNetworkRequestsVersion = -1;
    private long cachedVisualTreesVersion = -1;
    private long cachedArtifactsVersion = -1;
    private long cachedApplicationEventsVersion = -1;
    private long cachedLogsVersion = -1;
    private long cachedLogStreamsVersion = -1;
    private long cachedLiveReplayLogsVersion = -1;
    private long cachedMetricChannelsVersion = -1;
    private long cachedMetricsVersion = -1;
    private long cachedLiveMetricsVersion = -1;
    private long cachedLiveReplayMetricsVersion = -1;
    private IReadOnlyList<string> cachedTags = Array.Empty<string>();
    private IReadOnlyList<SessionAnalysisRecord> cachedAnalyses = Array.Empty<SessionAnalysisRecord>();
    private IReadOnlyList<SessionAnnotation> cachedAnnotations = Array.Empty<SessionAnnotation>();
    private IReadOnlyList<SessionAgentTaskLink> cachedAgentTaskLinks = Array.Empty<SessionAgentTaskLink>();
    private IReadOnlyList<SessionImageFrame> cachedImages = Array.Empty<SessionImageFrame>();
    private IReadOnlyList<SessionTouchInputRecord> cachedTouches = Array.Empty<SessionTouchInputRecord>();
    private IReadOnlyList<SessionNetworkRequest> cachedNetworkRequests = Array.Empty<SessionNetworkRequest>();
    private IReadOnlyList<SessionVisualTreeSnapshot> cachedVisualTrees = Array.Empty<SessionVisualTreeSnapshot>();
    private IReadOnlyList<SessionVisualTreeSnapshot> cachedVisualTreeSources = Array.Empty<SessionVisualTreeSnapshot>();
    private IReadOnlyList<SessionArtifactSnapshot> cachedArtifacts = Array.Empty<SessionArtifactSnapshot>();
    private IReadOnlyList<SessionApplicationEvent> cachedApplicationEvents = Array.Empty<SessionApplicationEvent>();
    private IReadOnlyList<LogEntry> cachedLogs = Array.Empty<LogEntry>();
    private IReadOnlyList<SessionLogStream> cachedLogStreams = Array.Empty<SessionLogStream>();
    private IReadOnlyList<LogEntry> cachedLiveReplayLogs = Array.Empty<LogEntry>();
    private IReadOnlyList<SessionLogStream> cachedLiveReplayLogStreams = Array.Empty<SessionLogStream>();
    private IReadOnlyList<SessionMetricChannel> cachedMetricChannels = Array.Empty<SessionMetricChannel>();
    private IReadOnlyList<SessionMetricSample> cachedMetrics = Array.Empty<SessionMetricSample>();
    private IReadOnlyList<SessionMetricSample> cachedLiveMetrics = Array.Empty<SessionMetricSample>();
    private IReadOnlyList<SessionMetricSample> cachedLiveReplayMetrics = Array.Empty<SessionMetricSample>();
    private readonly List<SessionMetricSample> liveMetrics = [];
    private DateTimeOffset? latestMetricCapturedAtUtc;

    public void MarkTagsChanged() => tagsVersion++;
    public void MarkAnalysesChanged() => analysesVersion++;
    public void MarkAnnotationsChanged() => annotationsVersion++;
    public void MarkAgentTaskLinksChanged() => agentTaskLinksVersion++;
    public void MarkImagesChanged() => imagesVersion++;
    public void MarkTouchesChanged() => touchesVersion++;
    public void MarkNetworkRequestsChanged() => networkRequestsVersion++;
    public void MarkVisualTreesChanged() => visualTreesVersion++;
    public void MarkArtifactsChanged() => artifactsVersion++;
    public void MarkApplicationEventsChanged() => applicationEventsVersion++;
    public void MarkLogsChanged() => logsVersion++;
    public void MarkMetricChannelsChanged() => metricChannelsVersion++;
    public void MarkMetricsChanged() => metricsVersion++;

    public void AddTouches(IReadOnlyList<SessionTouchInputRecord> touches)
    {
        if (touches.Count == 0)
        {
            return;
        }

        if (Touches.Count == 0 || CompareTouches(Touches[^1], touches[0]) <= 0)
        {
            Touches.AddRange(touches);
            return;
        }

        var mergedTouches = new List<SessionTouchInputRecord>(Touches.Count + touches.Count);
        var existingIndex = 0;
        var addedIndex = 0;
        while (existingIndex < Touches.Count && addedIndex < touches.Count)
        {
            if (CompareTouches(Touches[existingIndex], touches[addedIndex]) <= 0)
            {
                mergedTouches.Add(Touches[existingIndex++]);
            }
            else
            {
                mergedTouches.Add(touches[addedIndex++]);
            }
        }

        while (existingIndex < Touches.Count)
        {
            mergedTouches.Add(Touches[existingIndex++]);
        }

        while (addedIndex < touches.Count)
        {
            mergedTouches.Add(touches[addedIndex++]);
        }

        Touches.Clear();
        Touches.AddRange(mergedTouches);
    }

    public void AddMetrics(IReadOnlyList<SessionMetricSample> metrics)
    {
        if (metrics.Count == 0)
        {
            return;
        }

        Metrics.AddRange(metrics);

        var incomingLatestUtc = metrics
            .Select(metric => metric.CapturedAtUtc.ToUniversalTime())
            .Max();
        if (!latestMetricCapturedAtUtc.HasValue || incomingLatestUtc > latestMetricCapturedAtUtc.Value)
        {
            latestMetricCapturedAtUtc = incomingLatestUtc;
        }

        var earliestRetainedUtc = latestMetricCapturedAtUtc.Value - LiveMetricSnapshotWindow;
        var firstRetainedIndex = ResolveFirstMetricIndexAtOrAfter(liveMetrics, earliestRetainedUtc);
        if (firstRetainedIndex > 0)
        {
            liveMetrics.RemoveRange(0, firstRetainedIndex);
        }

        var retainedIncomingMetrics = metrics
            .Where(metric => metric.CapturedAtUtc.ToUniversalTime() >= earliestRetainedUtc)
            .ToList();
        retainedIncomingMetrics.Sort(CompareMetrics);
        MergeLiveMetrics(retainedIncomingMetrics);
        if (liveMetrics.Count > MaximumLiveMetricSnapshotCount)
        {
            liveMetrics.RemoveRange(0, liveMetrics.Count - MaximumLiveMetricSnapshotCount);
        }
    }

    private void MergeLiveMetrics(IReadOnlyList<SessionMetricSample> metrics)
    {
        if (metrics.Count == 0)
        {
            return;
        }

        if (liveMetrics.Count == 0 || CompareMetrics(liveMetrics[^1], metrics[0]) <= 0)
        {
            liveMetrics.AddRange(metrics);
            return;
        }

        var mergedMetrics = new List<SessionMetricSample>(liveMetrics.Count + metrics.Count);
        var existingIndex = 0;
        var addedIndex = 0;
        while (existingIndex < liveMetrics.Count && addedIndex < metrics.Count)
        {
            if (CompareMetrics(liveMetrics[existingIndex], metrics[addedIndex]) <= 0)
            {
                mergedMetrics.Add(liveMetrics[existingIndex++]);
            }
            else
            {
                mergedMetrics.Add(metrics[addedIndex++]);
            }
        }

        while (existingIndex < liveMetrics.Count)
        {
            mergedMetrics.Add(liveMetrics[existingIndex++]);
        }

        while (addedIndex < metrics.Count)
        {
            mergedMetrics.Add(metrics[addedIndex++]);
        }

        liveMetrics.Clear();
        liveMetrics.AddRange(mergedMetrics);
    }

    public void MarkAllContentChanged()
    {
        MarkAnalysesChanged();
        MarkAnnotationsChanged();
        MarkAgentTaskLinksChanged();
        MarkImagesChanged();
        MarkTouchesChanged();
        MarkNetworkRequestsChanged();
        MarkVisualTreesChanged();
        MarkArtifactsChanged();
        MarkApplicationEventsChanged();
        MarkLogsChanged();
        MarkMetricChannelsChanged();
        MarkMetricsChanged();
    }

    public IReadOnlyList<string> GetSnapshotTags()
    {
        if (cachedTagsVersion != tagsVersion)
        {
            cachedTags = Tags.ToArray();
            cachedTagsVersion = tagsVersion;
        }

        return cachedTags;
    }

    public IReadOnlyList<SessionAnalysisRecord> GetSnapshotAnalyses()
    {
        if (cachedAnalysesVersion != analysesVersion)
        {
            cachedAnalyses = Analyses.ToArray();
            cachedAnalysesVersion = analysesVersion;
        }

        return cachedAnalyses;
    }

    public IReadOnlyList<SessionAnnotation> GetSnapshotAnnotations()
    {
        if (cachedAnnotationsVersion != annotationsVersion)
        {
            cachedAnnotations = Annotations
                .Select(SessionSnapshotCloner.CloneAnnotation)
                .ToArray();
            cachedAnnotationsVersion = annotationsVersion;
        }

        return cachedAnnotations;
    }

    public IReadOnlyList<SessionAgentTaskLink> GetSnapshotAgentTaskLinks()
    {
        if (cachedAgentTaskLinksVersion != agentTaskLinksVersion)
        {
            cachedAgentTaskLinks = AgentTaskLinks
                .Select(static taskLink => SessionSnapshotCloner.CloneAgentTaskLink(taskLink))
                .ToArray();
            cachedAgentTaskLinksVersion = agentTaskLinksVersion;
        }

        return cachedAgentTaskLinks;
    }

    public IReadOnlyList<SessionImageFrame> GetSnapshotImages()
    {
        if (cachedImagesVersion != imagesVersion)
        {
            cachedImages = Images.ToArray();
            cachedImagesVersion = imagesVersion;
        }

        return cachedImages;
    }

    public IReadOnlyList<SessionTouchInputRecord> GetSnapshotTouches()
    {
        if (cachedTouchesVersion != touchesVersion)
        {
            cachedTouches = Touches.ToArray();
            cachedTouchesVersion = touchesVersion;
        }

        return cachedTouches;
    }

    public IReadOnlyList<SessionNetworkRequest> GetSnapshotNetworkRequests()
    {
        if (cachedNetworkRequestsVersion != networkRequestsVersion)
        {
            cachedNetworkRequests = NetworkRequests
                .OrderBy(static request => request.StartedAtUtc)
                .ThenBy(static request => request.Id, StringComparer.Ordinal)
                .ToArray();
            cachedNetworkRequestsVersion = networkRequestsVersion;
        }

        return cachedNetworkRequests;
    }

    public IReadOnlyList<SessionApplicationEvent> GetSnapshotApplicationEvents()
    {
        if (cachedApplicationEventsVersion != applicationEventsVersion)
        {
            cachedApplicationEvents = ApplicationEvents
                .OrderBy(static appEvent => appEvent.CapturedAtUtc)
                .ThenBy(static appEvent => appEvent.EventId, StringComparer.Ordinal)
                .ToArray();
            cachedApplicationEventsVersion = applicationEventsVersion;
        }

        return cachedApplicationEvents;
    }

    public IReadOnlyList<SessionVisualTreeSnapshot> GetSnapshotVisualTrees()
    {
        if (cachedVisualTreesVersion != visualTreesVersion)
        {
            var cachedIndexBySnapshotId = new Dictionary<string, int>(cachedVisualTreeSources.Count, StringComparer.Ordinal);
            for (var index = 0; index < cachedVisualTreeSources.Count; index++)
            {
                cachedIndexBySnapshotId[cachedVisualTreeSources[index].SnapshotId] = index;
            }

            var sources = new SessionVisualTreeSnapshot[VisualTreeSnapshots.Count];
            var snapshots = new SessionVisualTreeSnapshot[VisualTreeSnapshots.Count];
            for (var index = 0; index < VisualTreeSnapshots.Count; index++)
            {
                var source = VisualTreeSnapshots[index];
                sources[index] = source;
                snapshots[index] = cachedIndexBySnapshotId.TryGetValue(source.SnapshotId, out var cachedIndex)
                                   && ReferenceEquals(source, cachedVisualTreeSources[cachedIndex])
                    ? cachedVisualTrees[cachedIndex]
                    : SessionSnapshotCloner.CloneVisualTreeSnapshot(source);
            }

            cachedVisualTreeSources = sources;
            cachedVisualTrees = snapshots;
            cachedVisualTreesVersion = visualTreesVersion;
        }

        return cachedVisualTrees;
    }

    public IReadOnlyList<SessionArtifactSnapshot> GetSnapshotArtifacts()
    {
        if (cachedArtifactsVersion != artifactsVersion)
        {
            cachedArtifacts = ArtifactSnapshots
                .Select(SessionSnapshotCloner.CloneArtifactSnapshot)
                .ToArray();
            cachedArtifactsVersion = artifactsVersion;
        }

        return cachedArtifacts;
    }

    public IReadOnlyList<LogEntry> GetSnapshotLogs()
    {
        if (cachedLogsVersion != logsVersion)
        {
            cachedLogs = Logs.ToArray();
            cachedLogsVersion = logsVersion;
        }

        return cachedLogs;
    }

    public IReadOnlyList<SessionLogStream> GetSnapshotLogStreams()
    {
        if (cachedLogStreamsVersion != logsVersion)
        {
            cachedLogStreams = LogStreams.Values
                .Select(stream => stream.ToSnapshot())
                .OrderBy(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk ? 0 : 1)
                .ThenBy(stream => stream.DisplayName, StringComparer.Ordinal)
                .ThenBy(stream => stream.StreamId, StringComparer.Ordinal)
                .ToArray();
            cachedLogStreamsVersion = logsVersion;
        }

        return cachedLogStreams;
    }

    public IReadOnlyList<LogEntry> GetLiveReplaySnapshotLogs()
    {
        EnsureLiveReplayLogSnapshot();
        return cachedLiveReplayLogs;
    }

    public IReadOnlyList<SessionLogStream> GetLiveReplaySnapshotLogStreams()
    {
        EnsureLiveReplayLogSnapshot();
        return cachedLiveReplayLogStreams;
    }

    public IReadOnlyList<SessionLogStream> GetSnapshotLogStreamDescriptors()
        => LogStreams.Values
            .Select(stream => stream.ToSnapshot(includeEntries: false))
            .OrderBy(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk ? 0 : 1)
            .ThenBy(stream => stream.DisplayName, StringComparer.Ordinal)
            .ThenBy(stream => stream.StreamId, StringComparer.Ordinal)
            .ToArray();

    private void EnsureLiveReplayLogSnapshot()
    {
        if (cachedLiveReplayLogsVersion == logsVersion)
        {
            return;
        }

        cachedLiveReplayLogs = Logs.Count <= MaximumLiveReplayLogSnapshotCount
            ? Logs.ToArray()
            : Logs.GetRange(Logs.Count - MaximumLiveReplayLogSnapshotCount, MaximumLiveReplayLogSnapshotCount);
        cachedLiveReplayLogStreams = LogStreams.Values
            .Select(CreateLiveReplayLogStreamSnapshot)
            .OrderBy(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk ? 0 : 1)
            .ThenBy(stream => stream.DisplayName, StringComparer.Ordinal)
            .ThenBy(stream => stream.StreamId, StringComparer.Ordinal)
            .ToArray();
        cachedLiveReplayLogsVersion = logsVersion;
    }

    private SessionLogStream CreateLiveReplayLogStreamSnapshot(SessionLogStreamState stream)
    {
        var entries = cachedLiveReplayLogs
            .Where(log => string.Equals(log.StreamId, stream.StreamId, StringComparison.Ordinal))
            .ToArray();
        return new SessionLogStream
        {
            StreamId = stream.StreamId,
            Kind = stream.Kind,
            DisplayName = stream.DisplayName,
            Status = stream.Status,
            StartedUtc = stream.StartedUtc,
            EndedUtc = stream.EndedUtc,
            StatusMessage = stream.StatusMessage,
            Metadata = new Dictionary<string, string>(stream.Metadata, StringComparer.Ordinal),
            Entries = entries,
            TotalEntryCount = stream.TotalEntryCount,
            RetainedEntryStartIndex = Math.Max(0, stream.TotalEntryCount - entries.Length)
        };
    }

    public BoundedDeduplicationSet<string> GetSeenEventIds(string streamId)
    {
        if (!SeenEventIdsByStream.TryGetValue(streamId, out var eventIds))
        {
            eventIds = new BoundedDeduplicationSet<string>(32_768, StringComparer.Ordinal);
            SeenEventIdsByStream[streamId] = eventIds;
        }

        return eventIds;
    }

    public IReadOnlyList<SessionMetricChannel> GetSnapshotMetricChannels()
    {
        if (cachedMetricChannelsVersion != metricChannelsVersion)
        {
            cachedMetricChannels = MetricChannels.Values
                .OrderBy(channel => channel.ChannelId)
                .ToArray();
            cachedMetricChannelsVersion = metricChannelsVersion;
        }

        return cachedMetricChannels;
    }

    public IReadOnlyList<SessionMetricSample> GetSnapshotMetrics()
    {
        if (cachedMetricsVersion != metricsVersion)
        {
            cachedMetrics = Metrics.ToArray();
            cachedMetricsVersion = metricsVersion;
        }

        return cachedMetrics;
    }

    public IReadOnlyList<SessionMetricSample> GetLiveSnapshotMetrics()
    {
        if (cachedLiveMetricsVersion != metricsVersion)
        {
            cachedLiveMetrics = liveMetrics.ToArray();
            cachedLiveMetricsVersion = metricsVersion;
        }

        return cachedLiveMetrics;
    }

    public IReadOnlyList<SessionMetricSample> GetLiveReplaySnapshotMetrics()
    {
        if (cachedLiveReplayMetricsVersion != metricsVersion)
        {
            cachedLiveReplayMetrics = ReduceMetricSamples(Metrics, MaximumLiveReplayMetricSnapshotCount);
            cachedLiveReplayMetricsVersion = metricsVersion;
        }

        return cachedLiveReplayMetrics;
    }

    private static IReadOnlyList<SessionMetricSample> ReduceMetricSamples(
        IReadOnlyList<SessionMetricSample> metrics,
        int maximumCount)
    {
        if (metrics.Count <= maximumCount)
        {
            return metrics.ToArray();
        }

        var samplesBySeries = new Dictionary<MetricSeriesKey, List<SessionMetricSample>>();
        foreach (var metric in metrics)
        {
            var key = new MetricSeriesKey(metric.ChannelId, metric.SegmentId);
            if (!samplesBySeries.TryGetValue(key, out var samples))
            {
                samples = [];
                samplesBySeries[key] = samples;
            }

            samples.Add(metric);
        }

        var series = samplesBySeries
            .Select(pair => new MetricSeriesSelection
            {
                Key = pair.Key,
                Samples = pair.Value
                    .OrderBy(sample => sample.CapturedAtUtc)
                    .ToList()
            })
            .OrderBy(selection => selection.Key.ChannelId)
            .ThenBy(selection => selection.Key.SegmentId)
            .ToArray();
        var mandatoryCount = series.Sum(selection => Math.Min(2, selection.Samples.Count));
        if (mandatoryCount > maximumCount)
        {
            return series
                .SelectMany(selection => selection.Samples.Count == 1
                    ? new[] { selection.Samples[0] }
                    : new[] { selection.Samples[0], selection.Samples[^1] })
                .OrderBy(sample => sample.CapturedAtUtc)
                .ThenBy(sample => sample.ChannelId)
                .ThenBy(sample => sample.SegmentId)
                .Take(maximumCount)
                .ToArray();
        }

        var remainingCapacity = maximumCount - mandatoryCount;
        var selectableCount = metrics.Count - mandatoryCount;
        foreach (var selection in series)
        {
            var requiredCount = Math.Min(2, selection.Samples.Count);
            var availableCount = selection.Samples.Count - requiredCount;
            var exactAllocation = selectableCount == 0
                ? 0
                : (double)remainingCapacity * availableCount / selectableCount;
            var allocatedCount = Math.Min(availableCount, (int)Math.Floor(exactAllocation));
            selection.TargetCount = requiredCount + allocatedCount;
            selection.AllocationRemainder = exactAllocation - allocatedCount;
        }

        var unallocatedCount = maximumCount - series.Sum(selection => selection.TargetCount);
        foreach (var selection in series
                     .Where(selection => selection.TargetCount < selection.Samples.Count)
                     .OrderByDescending(selection => selection.AllocationRemainder)
                     .ThenBy(selection => selection.Key.ChannelId)
                     .ThenBy(selection => selection.Key.SegmentId))
        {
            if (unallocatedCount == 0)
            {
                break;
            }

            selection.TargetCount++;
            unallocatedCount--;
        }

        return series
            .SelectMany(selection => ReduceMetricSeries(selection.Samples, selection.TargetCount))
            .OrderBy(sample => sample.CapturedAtUtc)
            .ThenBy(sample => sample.ChannelId)
            .ThenBy(sample => sample.SegmentId)
            .ToArray();
    }

    private static IReadOnlyList<SessionMetricSample> ReduceMetricSeries(
        IReadOnlyList<SessionMetricSample> samples,
        int targetCount)
    {
        if (samples.Count <= targetCount)
        {
            return samples.ToArray();
        }

        if (targetCount <= 1)
        {
            return [samples[0]];
        }

        var selectedIndices = new HashSet<int> { 0, samples.Count - 1 };
        var interiorSampleCount = samples.Count - 2;
        var bucketCount = Math.Min(interiorSampleCount, Math.Max(0, (targetCount - 2) / 2));
        for (var bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            var startIndex = 1 + (bucketIndex * interiorSampleCount / bucketCount);
            var endIndex = 1 + ((bucketIndex + 1) * interiorSampleCount / bucketCount);
            var minimumIndex = startIndex;
            var maximumIndex = startIndex;
            for (var sampleIndex = startIndex + 1; sampleIndex < endIndex; sampleIndex++)
            {
                if (samples[sampleIndex].Value < samples[minimumIndex].Value)
                {
                    minimumIndex = sampleIndex;
                }

                if (samples[sampleIndex].Value > samples[maximumIndex].Value)
                {
                    maximumIndex = sampleIndex;
                }
            }

            selectedIndices.Add(minimumIndex);
            selectedIndices.Add(maximumIndex);
        }

        for (var selectionIndex = 1; selectedIndices.Count < targetCount && selectionIndex < targetCount - 1; selectionIndex++)
        {
            selectedIndices.Add((int)Math.Round((double)selectionIndex * (samples.Count - 1) / (targetCount - 1)));
        }

        for (var sampleIndex = 1; selectedIndices.Count < targetCount && sampleIndex < samples.Count - 1; sampleIndex++)
        {
            selectedIndices.Add(sampleIndex);
        }

        return selectedIndices
            .OrderBy(index => index)
            .Take(targetCount)
            .Select(index => samples[index])
            .ToArray();
    }

    private readonly record struct MetricSeriesKey(byte ChannelId, int SegmentId);

    private sealed class MetricSeriesSelection
    {
        public required MetricSeriesKey Key { get; init; }
        public required List<SessionMetricSample> Samples { get; init; }
        public int TargetCount { get; set; }
        public double AllocationRemainder { get; set; }
    }

    private static int CompareTouches(SessionTouchInputRecord left, SessionTouchInputRecord right)
    {
        var timestampComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        return timestampComparison != 0
            ? timestampComparison
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static int CompareMetrics(SessionMetricSample left, SessionMetricSample right)
    {
        var timestampComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        return timestampComparison != 0
            ? timestampComparison
            : left.ChannelId.CompareTo(right.ChannelId);
    }

    private static int ResolveFirstMetricIndexAtOrAfter(
        IReadOnlyList<SessionMetricSample> metrics,
        DateTimeOffset capturedAtUtc)
    {
        var low = 0;
        var high = metrics.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (metrics[middle].CapturedAtUtc < capturedAtUtc)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}
