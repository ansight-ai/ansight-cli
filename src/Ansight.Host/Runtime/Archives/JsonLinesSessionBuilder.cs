using System.Text.Json.Nodes;
using Ansight.Pairing.Models;
using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Archives;

internal sealed class JsonLinesSessionBuilder
{
    private readonly string fallbackSessionName;
    private readonly List<LogEntry> logs = [];
    private readonly List<SessionMetricChannel> metricChannels = [];
    private readonly List<SessionMetricSample> metrics = [];
    private readonly List<SessionApplicationEvent> applicationEvents = [];
    private readonly List<SessionTouchInputRecord> touches = [];
    private readonly List<SessionNetworkRequest> networkRequests = [];
    private readonly List<SessionImageFrame> images = [];
    private readonly Dictionary<string, byte[]> imageBytesByFrameId = new(StringComparer.Ordinal);
    private readonly List<SessionAnnotation> annotations = [];
    private readonly List<SessionVisualTreeSnapshot> visualTreeSnapshots = [];
    private readonly List<SessionArtifactSnapshot> artifactSnapshots = [];
    private readonly Dictionary<string, byte[]> artifactBytesByRelativePath = new(StringComparer.Ordinal);
    private AppSessionSnapshot? snapshot;

    public JsonLinesSessionBuilder(string? fallbackSessionName)
    {
        this.fallbackSessionName = string.IsNullOrWhiteSpace(fallbackSessionName)
            ? $"offline-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : fallbackSessionName.Trim();
    }

    public bool HasAnyEvidence => snapshot is not null
                                  || !string.IsNullOrWhiteSpace(SessionId)
                                  || !string.IsNullOrWhiteSpace(AppId)
                                  || logs.Count > 0
                                  || metricChannels.Count > 0
                                  || metrics.Count > 0
                                  || applicationEvents.Count > 0
                                  || touches.Count > 0
                                  || networkRequests.Count > 0
                                  || images.Count > 0
                                  || annotations.Count > 0
                                  || visualTreeSnapshots.Count > 0
                                  || artifactSnapshots.Count > 0;

    public bool HasLogs => logs.Count > 0;
    public bool HasMetricChannels => metricChannels.Count > 0;
    public bool HasMetrics => metrics.Count > 0;
    public bool HasApplicationEvents => applicationEvents.Count > 0;
    public bool HasTouches => touches.Count > 0;
    public bool HasNetworkRequests => networkRequests.Count > 0;

    public IReadOnlyList<LogEntry> Logs => logs
        .OrderBy(log => log.TimestampUtc)
        .ToArray();

    public IReadOnlyList<SessionMetricChannel> MetricChannels => metricChannels
        .GroupBy(channel => channel.ChannelId)
        .Select(group => group.Last())
        .OrderBy(channel => channel.ChannelId)
        .ToArray();

    public IReadOnlyList<SessionMetricSample> Metrics => metrics
        .OrderBy(metric => metric.CapturedAtUtc)
        .ThenBy(metric => metric.ChannelId)
        .ToArray();

    public IReadOnlyList<SessionApplicationEvent> ApplicationEvents => applicationEvents
        .GroupBy(appEvent => appEvent.EventId, StringComparer.Ordinal)
        .Select(static group => group.Last())
        .OrderBy(static appEvent => appEvent.CapturedAtUtc)
        .ThenBy(static appEvent => appEvent.EventId, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<SessionTouchInputRecord> Touches => touches
        .OrderBy(touch => touch.CapturedAtUtc)
        .ThenBy(touch => touch.Id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<SessionNetworkRequest> NetworkRequests => networkRequests
        .GroupBy(static request => request.Id, StringComparer.Ordinal)
        .Select(static group => group.Last())
        .OrderBy(static request => request.StartedAtUtc)
        .ThenBy(static request => request.Id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<SessionImageFrame> Images => images
        .OrderBy(image => image.CapturedAtUtc)
        .ThenBy(image => image.FrameId, StringComparer.Ordinal)
        .ToArray();

    public string? SessionId { get; set; }
    public string? AppId { get; set; }
    public string? ClientName { get; set; }
    public string? RemoteAddress { get; set; }
    public string? ConfigId { get; set; }
    public string? ProcessSessionId { get; set; }
    public string? Status { get; set; }
    public string? CaptureSource { get; set; }

    public string? SdkVersion { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }
    public AppLifecycleState? AppState { get; set; }
    public DateTimeOffset? AppStateChangedUtc { get; set; }
    public DeviceAppProfile? DeviceProfile { get; set; }
    public string? DeviceProfileJson { get; set; }
    public JsonObject? CustomProperties { get; set; }

    public void ApplySnapshot(AppSessionSnapshot value)
    {
        snapshot = value;
    }

    public void AddLog(LogEntry log)
    {
        logs.Add(log);
    }

    public void AddMetricChannel(SessionMetricChannel channel)
    {
        metricChannels.RemoveAll(candidate => candidate.ChannelId == channel.ChannelId);
        metricChannels.Add(channel);
    }

    public void AddMetric(SessionMetricSample sample)
    {
        metrics.Add(sample);
    }

    public void AddApplicationEvent(SessionApplicationEvent appEvent)
    {
        applicationEvents.RemoveAll(candidate => string.Equals(
            candidate.EventId,
            appEvent.EventId,
            StringComparison.Ordinal));
        applicationEvents.Add(appEvent);
    }

    public void AddTouch(SessionTouchInputRecord touch)
    {
        touches.Add(touch);
    }

    public void AddNetworkRequest(SessionNetworkRequest request)
    {
        var normalized = SessionNetworkRequestSanitizer.Normalize(request);
        if (normalized is not null)
        {
            networkRequests.Add(normalized);
        }
    }

    public void AddImage(SessionImageFrame image, byte[] bytes)
    {
        images.RemoveAll(candidate => string.Equals(candidate.FrameId, image.FrameId, StringComparison.Ordinal));
        images.Add(image);
        imageBytesByFrameId[image.FrameId] = bytes;
    }

    public void AddAnnotatedFeedback(AnnotatedFeedbackBundleContent content)
    {
        var screenshotFrame = content.CreateScreenshotFrame();
        if (screenshotFrame is not null && content.Screenshot is not null)
        {
            AddImage(screenshotFrame, content.Screenshot.Bytes);
        }

        var screenshotFrameId = screenshotFrame?.FrameId;
        annotations.RemoveAll(annotation => string.Equals(annotation.AnnotationId, content.AnnotationId, StringComparison.Ordinal));
        annotations.Add(content.CreateAnnotation(screenshotFrameId));

        foreach (var visualTreeSnapshot in content.CreateVisualTreeSnapshots(screenshotFrameId))
        {
            visualTreeSnapshots.RemoveAll(snapshot => string.Equals(snapshot.SnapshotId, visualTreeSnapshot.SnapshotId, StringComparison.Ordinal));
            visualTreeSnapshots.Add(visualTreeSnapshot);
        }

        var artifactSnapshot = content.CreateArtifactSnapshot();
        if (artifactSnapshot is null)
        {
            return;
        }

        artifactSnapshots.RemoveAll(snapshot => string.Equals(snapshot.SnapshotId, artifactSnapshot.SnapshotId, StringComparison.Ordinal));
        artifactSnapshots.Add(artifactSnapshot);
        foreach (var artifact in content.Artifacts.Where(artifact => artifact.Bytes is { Length: > 0 }))
        {
            artifactBytesByRelativePath[$"{artifactSnapshot.ArtifactDirectoryName}/{artifact.FileName}"] = artifact.Bytes!;
        }
    }

    public SessionArchiveJsonLinesPayload Build(string? sourceName)
    {
        var evidenceStartUtc = EarliestEvidenceUtc();
        var evidenceEndUtc = LatestEvidenceUtc();
        var now = DateTimeOffset.UtcNow;
        var createdUtc = CreatedUtc
                         ?? snapshot?.CreatedUtc
                         ?? evidenceStartUtc
                         ?? now;
        var lastUpdatedUtc = LastUpdatedUtc
                             ?? snapshot?.LastUpdatedUtc
                             ?? evidenceEndUtc
                             ?? createdUtc;

        var resolvedSessionId = NormalizeIdentifier(
            SessionId
            ?? snapshot?.SessionId
            ?? sourceName
            ?? fallbackSessionName,
            $"offline-{now:yyyyMMddHHmmss}");
        var resolvedAppId = NormalizeIdentifier(
            AppId
            ?? snapshot?.AppId
            ?? "offline.capture",
            "offline.capture");

        var builtLogs = HasLogs ? Logs : snapshot?.Logs ?? Array.Empty<LogEntry>();
        var builtLogStreams = SessionLogStreams.Normalize(
            HasLogs ? Array.Empty<SessionLogStream>() : snapshot?.LogStreams,
            builtLogs);
        var builtApplicationEvents = (snapshot?.ApplicationEvents ?? Array.Empty<SessionApplicationEvent>())
            .Concat(ApplicationEvents)
            .GroupBy(static appEvent => appEvent.EventId, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static appEvent => appEvent.CapturedAtUtc)
            .ToArray();
        var builtSnapshot = new AppSessionSnapshot
        {
            SessionId = resolvedSessionId,
            AppId = resolvedAppId,
            ClientName = NormalizeText(ClientName ?? snapshot?.ClientName) ?? "Offline Capture",
            RemoteAddress = NormalizeText(RemoteAddress ?? snapshot?.RemoteAddress) ?? "offline",
            CreatedUtc = createdUtc,
            ConfigId = ConfigId ?? snapshot?.ConfigId,
            ProcessSessionId = ProcessSessionId ?? snapshot?.ProcessSessionId,
            Status = NormalizeText(Status ?? snapshot?.Status) ?? "Imported Offline Capture",
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = snapshot?.CacheSizeBytes ?? 0,
            IsPinned = snapshot?.IsPinned ?? false,
            Author = snapshot?.Author,
            ReplaySource = snapshot?.ReplaySource,
            CaptureSource = CaptureSource ?? snapshot?.CaptureSource ?? WorkspaceExecutionModes.Sdk,
            SdkVersion = SdkVersion ?? snapshot?.SdkVersion,
            Name = snapshot?.Name,
            Tags = snapshot?.Tags ?? Array.Empty<string>(),
            Notes = Notes ?? snapshot?.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(CustomProperties ?? snapshot?.CustomProperties),
            AppState = AppState ?? snapshot?.AppState ?? AppLifecycleState.Unknown,
            AppStateChangedUtc = AppStateChangedUtc ?? snapshot?.AppStateChangedUtc,
            DeviceProfile = DeviceProfile ?? snapshot?.DeviceProfile,
            DeviceProfileJson = DeviceProfileJson ?? snapshot?.DeviceProfileJson,
            AppIcon = null,
            Analyses = snapshot?.Analyses ?? Array.Empty<SessionAnalysisRecord>(),
            Annotations = (snapshot?.Annotations ?? Array.Empty<SessionAnnotation>())
                .Concat(annotations)
                .GroupBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(annotation => annotation.StartUtc)
                .ToArray(),
            AgentTaskLinks = snapshot?.AgentTaskLinks ?? Array.Empty<SessionAgentTaskLink>(),
            Images = Images,
            Touches = HasTouches ? Touches : snapshot?.Touches ?? Array.Empty<SessionTouchInputRecord>(),
            NetworkRequests = HasNetworkRequests ? NetworkRequests : snapshot?.NetworkRequests ?? Array.Empty<SessionNetworkRequest>(),
            VisualTreeSnapshots = (snapshot?.VisualTreeSnapshots ?? Array.Empty<SessionVisualTreeSnapshot>())
                .Concat(visualTreeSnapshots)
                .GroupBy(item => item.SnapshotId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(item => item.CapturedAtUtc)
                .ToArray(),
            ArtifactSnapshots = (snapshot?.ArtifactSnapshots ?? Array.Empty<SessionArtifactSnapshot>())
                .Concat(artifactSnapshots)
                .GroupBy(item => item.SnapshotId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(item => item.CapturedAtUtc)
                .ToArray(),
            ApplicationEvents = builtApplicationEvents,
            LogStreams = builtLogStreams,
            Logs = SessionLogStreams.Flatten(builtLogStreams),
            MetricChannels = HasMetricChannels ? MetricChannels : snapshot?.MetricChannels ?? Array.Empty<SessionMetricChannel>(),
            Metrics = HasMetrics ? Metrics : snapshot?.Metrics ?? Array.Empty<SessionMetricSample>(),
            TotalApplicationEventCount = Math.Max(
                snapshot?.TotalApplicationEventCount ?? 0,
                builtApplicationEvents.Length),
            TotalNetworkRequestCount = Math.Max(
                snapshot?.TotalNetworkRequestCount ?? 0,
                HasNetworkRequests ? NetworkRequests.Count : snapshot?.NetworkRequests.Count ?? 0)
        };

        return new SessionArchiveJsonLinesPayload
        {
            Snapshot = SessionArchiveExternalPayloadReader.RemoveImagesWithoutPayloads(builtSnapshot, imageBytesByFrameId),
            ImageBytesByFrameId = new Dictionary<string, byte[]>(imageBytesByFrameId, StringComparer.Ordinal),
            ArtifactBytesByRelativePath = new Dictionary<string, byte[]>(artifactBytesByRelativePath, StringComparer.Ordinal)
        };
    }

    private DateTimeOffset? EarliestEvidenceUtc()
    {
        var timestamps = logs.Select(log => log.TimestampUtc)
            .Concat(metrics.Select(metric => metric.CapturedAtUtc))
            .Concat(applicationEvents.Select(appEvent => appEvent.CapturedAtUtc))
            .Concat(touches.Select(touch => touch.CapturedAtUtc))
            .Concat(networkRequests.Select(request => request.StartedAtUtc))
            .Concat(images.Select(image => image.CapturedAtUtc))
            .Concat(annotations.Select(annotation => annotation.StartUtc))
            .Concat(visualTreeSnapshots.Select(snapshot => snapshot.CapturedAtUtc))
            .Concat(artifactSnapshots.Select(snapshot => snapshot.CapturedAtUtc))
            .Where(timestamp => timestamp != default)
            .OrderBy(timestamp => timestamp)
            .ToArray();
        return timestamps.Length == 0 ? null : timestamps[0];
    }

    private DateTimeOffset? LatestEvidenceUtc()
    {
        var timestamps = logs.Select(log => log.TimestampUtc)
            .Concat(metrics.Select(metric => metric.CapturedAtUtc))
            .Concat(applicationEvents.Select(appEvent => appEvent.CapturedAtUtc))
            .Concat(touches.Select(touch => touch.CapturedAtUtc))
            .Concat(networkRequests.Select(request => request.CompletedAtUtc))
            .Concat(images.Select(image => image.CapturedAtUtc))
            .Concat(annotations.Select(annotation => annotation.StartUtc))
            .Concat(visualTreeSnapshots.Select(snapshot => snapshot.CapturedAtUtc))
            .Concat(artifactSnapshots.Select(snapshot => snapshot.CapturedAtUtc))
            .Where(timestamp => timestamp != default)
            .OrderByDescending(timestamp => timestamp)
            .ToArray();
        return timestamps.Length == 0 ? null : timestamps[0];
    }

    private static string NormalizeIdentifier(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
