namespace Ansight.Host.Runtime.SessionCaptureStorage;

using System.Security.Cryptography;
using System.Text.Json;
using Ansight.Host;
using Ansight.Pairing.Models;
using static SessionCaptureFileSystem;

internal sealed partial class SessionCaptureStore
{
    public void SaveAndPruneDetachedContent(AppSessionSnapshot snapshot, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var snapshotCopy = SessionSnapshotReader.CloneAsHistorical(snapshot);
        var layout = GetSessionLayout(snapshotCopy.AppId, snapshotCopy.SessionId);

        RunWithSessionPersistenceLock(snapshotCopy.SessionId, () =>
        {
            if (IsDeleted(snapshotCopy.SessionId))
            {
                return;
            }

            report?.Invoke("Removing detached evidence files…");
            PruneDetachedSessionContent(layout, snapshotCopy);
            report?.Invoke("Saving retained session evidence…");
            SaveSnapshot(snapshotCopy, report);
        });
    }

    private void SaveSnapshot(AppSessionSnapshot snapshot, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var layout = GetSessionLayout(snapshot.AppId, snapshot.SessionId);
        Directory.CreateDirectory(layout.AppDirectoryPath);
        Directory.CreateDirectory(layout.SessionDirectoryPath);
        Directory.CreateDirectory(layout.TelemetryDirectoryPath);
        Directory.CreateDirectory(layout.CapturedImagesDirectoryPath);
        Directory.CreateDirectory(layout.ArtifactsDirectoryPath);
        Directory.CreateDirectory(layout.NetworkRequestsDirectoryPath);

        var savedAtUtc = DateTimeOffset.UtcNow;
        SessionPersistenceCounts persistenceCounts;
        SessionIncrementalPersistenceState? incrementalPersistenceState = null;
        var shouldPreserveIncrementalContent = !IsRecordingStatus(snapshot.Status)
                                               && IsRecordingStatus(ResolvePersistedStatus(snapshot));
        if (IsRecordingStatus(snapshot.Status))
        {
            incrementalPersistenceState = WriteIncrementalRecordingSnapshotContent(layout, savedAtUtc, snapshot);
            persistenceCounts = new SessionPersistenceCounts(
                Math.Max(ResolveTotalLogCount(snapshot), incrementalPersistenceState.PersistedLogCount),
                Math.Max(snapshot.TotalImageCount, incrementalPersistenceState.PersistedImageCount),
                Math.Max(snapshot.Touches.Count, incrementalPersistenceState.PersistedTouchInputCount),
                Math.Max(snapshot.TotalMetricSampleCount, incrementalPersistenceState.PersistedMetricSampleCount));
        }
        else
        {
            persistenceCounts = WriteFullSnapshotContent(
                layout,
                savedAtUtc,
                snapshot,
                shouldPreserveIncrementalContent,
                report);
        }

        report?.Invoke("Updating session summary…");
        var appIcon = SessionSnapshotReader.LoadAppIcon(layout.SessionDirectoryPath, snapshot.AppIcon);
        var cacheSizeBytes = ResolveCacheSizeBytesForSnapshot(snapshot, layout);

        var summary = new SessionCaptureSummary
        {
            SessionId = snapshot.SessionId,
            AppId = snapshot.AppId,
            ClientName = snapshot.ClientName,
            RemoteAddress = snapshot.RemoteAddress,
            Name = snapshot.Name,
            CreatedUtc = snapshot.CreatedUtc,
            ConfigId = snapshot.ConfigId,
            ProcessSessionId = snapshot.ProcessSessionId,
            Status = snapshot.Status,
            LastUpdatedUtc = snapshot.LastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = cacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = SessionSdkVersion.Resolve(snapshot.SdkVersion, snapshot.DeviceProfile, snapshot.DeviceProfileJson),
            Tags = snapshot.Tags.ToArray(),
            Notes = snapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            AppIcon = appIcon,
            HasDeviceProfile = snapshot.DeviceProfile is not null || !string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson),
            AnalysisCount = snapshot.Analyses.Count,
            AnnotationCount = snapshot.Annotations.Count,
            ImageCount = persistenceCounts.ImageCount,
            TouchInputCount = persistenceCounts.TouchInputCount,
            VisualTreeSnapshotCount = snapshot.VisualTreeSnapshots.Count,
            ArtifactSnapshotCount = snapshot.ArtifactSnapshots.Count,
            LogCount = persistenceCounts.LogCount,
            MetricChannelCount = Math.Max(snapshot.TotalMetricChannelCount, snapshot.MetricChannels.Count),
            MetricSampleCount = persistenceCounts.MetricSampleCount,
            ApplicationEventCount = Math.Max(snapshot.TotalApplicationEventCount, snapshot.ApplicationEvents.Count),
            NetworkRequestCount = Math.Max(snapshot.TotalNetworkRequestCount, snapshot.NetworkRequests.Count)
        };
        var summaryDocument = new SessionCaptureSummaryDocument
        {
            SavedAtUtc = savedAtUtc,
            Session = summary
        };
        var summarySnapshot = CreateSummarySnapshot(summary, snapshot, appIcon);

        WriteJsonAtomic(layout.SummaryFilePath, summaryDocument);
        DeleteIfExists(SessionCaptureArchiveCodec.GetSessionArchivePath(layout.SessionDirectoryPath));
        var capturesWriteUtc = TouchCapturesIndex();
        persistedStatusBySessionId[snapshot.SessionId] = new PersistedSessionStatus(snapshot.Status);

        lock (indexGate)
        {
            EnsureIndex();
            archivePathBySessionId!.Remove(snapshot.SessionId);
            filePathBySessionId![snapshot.SessionId] = layout.SummaryFilePath;
            summaryBySessionId![snapshot.SessionId] = summary;
            summarySnapshotBySessionId![snapshot.SessionId] = summarySnapshot;
            lastObservedCapturesWriteUtc = capturesWriteUtc;
            if (!IsRecordingStatus(snapshot.Status))
            {
                cacheSizeBytesBySessionId![snapshot.SessionId] = cacheSizeBytes;
            }
            else
            {
                cacheSizeBytesBySessionId!.Remove(snapshot.SessionId);
            }
        }
    }

    private static AppSessionSnapshot CreateSummarySnapshot(
        SessionCaptureSummary summary,
        AppSessionSnapshot snapshot,
        SessionAppIcon? appIcon)
    {
        return new AppSessionSnapshot
        {
            SessionId = summary.SessionId,
            AppId = summary.AppId,
            ClientName = summary.ClientName,
            RemoteAddress = summary.RemoteAddress,
            Name = summary.Name,
            CreatedUtc = summary.CreatedUtc,
            ConfigId = summary.ConfigId,
            ProcessSessionId = summary.ProcessSessionId,
            Status = summary.Status,
            LastUpdatedUtc = summary.LastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = summary.CacheSizeBytes,
            IsPinned = summary.IsPinned,
            Author = summary.Author,
            ReplaySource = summary.ReplaySource,
            CaptureSource = summary.CaptureSource,
            SdkVersion = summary.SdkVersion,
            Tags = summary.Tags.ToArray(),
            Notes = summary.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(summary.CustomProperties),
            AppState = summary.AppState,
            AppStateChangedUtc = summary.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = appIcon,
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = snapshot.Images,
            NetworkRequests = snapshot.NetworkRequests,
            ApplicationEvents = snapshot.ApplicationEvents,
            TotalLogCount = summary.LogCount,
            RetainedLogStartIndex = Math.Max(0, summary.LogCount),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>(),
            TotalAnnotationCount = summary.AnnotationCount,
            TotalImageCount = summary.ImageCount,
            TotalMetricChannelCount = summary.MetricChannelCount,
            TotalMetricSampleCount = summary.MetricSampleCount,
            TotalApplicationEventCount = summary.ApplicationEventCount,
            TotalNetworkRequestCount = summary.NetworkRequestCount
        };
    }

    private SessionPersistenceCounts WriteFullSnapshotContent(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot,
        bool preserveIncrementalContent,
        Action<string>? report = null)
    {
        var compactedLogs = preserveIncrementalContent
            ? SessionPersistenceContentMerger.BuildCompactedLogs(layout, snapshot)
            : SessionPersistenceContentMerger.EnsureTimestampOrder(snapshot.Logs);
        var compactedImages = preserveIncrementalContent
            ? SessionPersistenceContentMerger.BuildCompactedImages(layout, snapshot)
            : snapshot.Images;
        var compactedTouches = preserveIncrementalContent
            ? SessionPersistenceContentMerger.BuildCompactedTouches(layout, snapshot)
            : snapshot.Touches;
        var compactedMetrics = preserveIncrementalContent
            ? SessionPersistenceContentMerger.BuildCompactedMetrics(layout, snapshot)
            : snapshot.Metrics;
        report?.Invoke("Saving retained logs…");
        WriteLogStreamDescriptors(layout, savedAtUtc, snapshot);
        WriteJsonAtomic(
            layout.LogsFilePath,
            new SessionLogsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Logs = compactedLogs
            });

        DeleteLogSegments(layout);
        report?.Invoke("Saving screenshots, trees and annotations…");
        WriteCommonSnapshotContent(
            layout,
            savedAtUtc,
            snapshot,
            compactedImages,
            compactedTouches);
        report?.Invoke("Saving retained telemetry…");
        WriteTelemetryBlobs(
            layout,
            savedAtUtc,
            snapshot.MetricChannels,
            compactedMetrics);
        incrementalPersistenceStateBySessionId.TryRemove(snapshot.SessionId, out _);
        foreach (var key in persistedLogCountByStream.Keys
                     .Where(key => string.Equals(key.SessionId, snapshot.SessionId, StringComparison.Ordinal))
                     .ToArray())
        {
            persistedLogCountByStream.TryRemove(key, out _);
        }

        return new SessionPersistenceCounts(
            compactedLogs.Count,
            compactedImages.Count,
            compactedTouches.Count,
            compactedMetrics.Count);
    }

    private SessionIncrementalPersistenceState WriteIncrementalRecordingSnapshotContent(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot)
    {
        var persistenceState = GetOrCreateIncrementalPersistenceState(layout, snapshot);

        WriteLogStreamDescriptors(layout, savedAtUtc, snapshot);
        WriteIncrementalLogSegments(layout, savedAtUtc, snapshot, persistenceState);
        WriteIncrementalSnapshotContent(layout, savedAtUtc, snapshot, persistenceState);
        WriteIncrementalTelemetrySegments(layout, savedAtUtc, snapshot, persistenceState);
        return persistenceState;
    }

    private static void WriteLogStreamDescriptors(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot)
    {
        var streams = SessionLogStreams.Normalize(snapshot.LogStreams, snapshot.Logs)
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
                Entries = Array.Empty<LogEntry>(),
                TotalEntryCount = Math.Max(stream.TotalEntryCount, stream.Entries.Count),
                RetainedEntryStartIndex = Math.Max(0, stream.TotalEntryCount)
            })
            .ToArray();
        WriteJsonAtomic(
            layout.LogStreamsFilePath,
            new SessionLogStreamsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Streams = streams
            });
    }

    private static void WriteCommonSnapshotContent(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionImageFrame> images,
        IReadOnlyList<SessionTouchInputRecord> touches)
    {
        WriteJsonAtomic(
            layout.AnalysesFilePath,
            new SessionAnalysesBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Analyses = snapshot.Analyses.ToArray()
            });

        WriteJsonAtomic(
            layout.AnnotationsFilePath,
            new SessionAnnotationsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Annotations = snapshot.Annotations.ToArray()
            });

        WriteJsonAtomic(
            layout.AgentTaskLinksFilePath,
            new SessionAgentTaskLinksBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Tasks = snapshot.AgentTaskLinks.ToArray()
            });

        WriteJsonAtomic(
            layout.ImagesFilePath,
            new SessionImagesBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Images = images.ToArray()
            });
        DeleteIfExists(GetImagesAppendFilePath(layout));

        WriteJsonAtomic(
            layout.TouchesFilePath,
            new SessionTouchesBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Batches = SessionTouchPacking.Pack(touches)
            },
            JsonUtil.Compact);
        DeleteIfExists(GetTouchesAppendFilePath(layout));

        WriteJsonAtomic(
            layout.ApplicationEventsFilePath,
            new SessionApplicationEventsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Events = snapshot.ApplicationEvents.ToArray()
            });

        ReconcileNetworkRequestDocuments(layout.NetworkRequestsDirectoryPath, snapshot.NetworkRequests);

        WriteVisualTreeSnapshotDocuments(layout, savedAtUtc, snapshot.VisualTreeSnapshots);
        WriteArtifactSnapshotDocuments(layout, savedAtUtc, snapshot.ArtifactSnapshots);

        WriteJsonAtomic(
            layout.MetricChannelsFilePath,
            new SessionMetricChannelsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Channels = snapshot.MetricChannels.ToArray()
            });

        WriteDeviceProfileBlob(layout.DeviceProfileFilePath, savedAtUtc, snapshot);
        WriteAppToolCatalogBlob(layout.AppToolCatalogFilePath, savedAtUtc, snapshot.AppToolCatalog);
    }

    private SessionIncrementalPersistenceState GetOrCreateIncrementalPersistenceState(
        SessionPathLayout layout,
        AppSessionSnapshot snapshot)
    {
        if (incrementalPersistenceStateBySessionId.TryGetValue(snapshot.SessionId, out var existingState))
        {
            return existingState;
        }

        var persistedSummary = SessionSnapshotReader.TryLoadDocument<SessionCaptureSummaryDocument>(layout.SummaryFilePath)?.Session;
        var persistedLogCount = SessionSnapshotReader.LoadLogs(layout.LogsFilePath, layout.LogSegmentsDirectoryPath).Count;
        var persistedImages = SessionSnapshotReader.LoadImages(
            layout.ImagesFilePath,
            layout.CapturedImagesDirectoryPath);
        var persistedMetrics = SessionSnapshotReader.LoadTelemetry(layout.TelemetryDirectoryPath);
        var persistedTouches = SessionSnapshotReader.LoadTouches(layout);
        var persistedNetworkRequests = SessionSnapshotReader.LoadNetworkRequests(layout.NetworkRequestsDirectoryPath);
        var persistedAppToolCatalog = SessionSnapshotReader
            .TryLoadDocument<SessionAppToolCatalogBlobDocument>(layout.AppToolCatalogFilePath)
            ?.Catalog;
        var state = new SessionIncrementalPersistenceState
        {
            PersistedLogCount = Math.Max(
                persistedSummary?.LogCount ?? 0,
                persistedLogCount),
            PersistedMetricSampleCount = Math.Max(
                persistedSummary?.MetricSampleCount ?? 0,
                persistedMetrics.Count),
            PersistedAnalysisCount = Math.Min(Math.Max(0, persistedSummary?.AnalysisCount ?? 0), snapshot.Analyses.Count),
            PersistedImageCount = Math.Max(persistedSummary?.ImageCount ?? 0, persistedImages.Count),
            PersistedTouchInputCount = Math.Max(persistedSummary?.TouchInputCount ?? 0, persistedTouches.Count),
            PersistedImageFrameIds = persistedImages
                .Select(frame => frame.FrameId)
                .Where(frameId => !string.IsNullOrWhiteSpace(frameId))
                .ToHashSet(StringComparer.Ordinal),
            PersistedTouchIds = persistedTouches
                .Select(touch => touch.Id)
                .Where(touchId => !string.IsNullOrWhiteSpace(touchId))
                .ToHashSet(StringComparer.Ordinal),
            PersistedNetworkRequestIds = persistedNetworkRequests
                .Select(static request => request.Id)
                .Where(static requestId => !string.IsNullOrWhiteSpace(requestId))
                .ToHashSet(StringComparer.Ordinal),
            PersistedAppToolCatalogSchema = persistedAppToolCatalog?.Schema,
            PersistedAppToolCatalogCapturedAtUtc = persistedAppToolCatalog?.CapturedAtUtc,
            PersistedVisualTreeSnapshots = LoadPersistedVisualTreeSnapshotDocumentState(layout.VisualTreesDirectoryPath),
            PersistedArtifactSnapshots = LoadPersistedArtifactSnapshotDocumentState(layout.ArtifactsDirectoryPath)
        };
        incrementalPersistenceStateBySessionId[snapshot.SessionId] = state;
        return state;
    }

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


internal sealed partial class SessionCaptureStore
{

    private static void WriteIncrementalLogSegments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot,
        SessionIncrementalPersistenceState persistenceState)
    {
        var totalLogCount = ResolveTotalLogCount(snapshot);
        var retainedLogStartIndex = ResolveRetainedLogStartIndex(snapshot);
        if (totalLogCount < persistenceState.PersistedLogCount)
        {
            // Log totals are monotonic for a session. A lower total means this snapshot was
            // queued before a newer native-log batch, so its log view is stale. Preserve the
            // newer journal and let a subsequent snapshot advance the remaining metadata.
            return;
        }

        if (persistenceState.PersistedLogCount >= totalLogCount)
        {
            return;
        }

        var firstUnpersistedRetainedIndex = persistenceState.PersistedLogCount - retainedLogStartIndex;
        if (firstUnpersistedRetainedIndex < 0 || firstUnpersistedRetainedIndex >= snapshot.Logs.Count)
        {
            return;
        }

        var addedLogCount = snapshot.Logs.Count - firstUnpersistedRetainedIndex;

        AppendJsonLine(
            GetLogsAppendFilePath(layout),
            new SessionLogsBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Logs = CopyRange(snapshot.Logs, firstUnpersistedRetainedIndex, addedLogCount)
            });
        persistenceState.PersistedLogCount = retainedLogStartIndex + snapshot.Logs.Count;
    }

    private static void WriteIncrementalSnapshotContent(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot,
        SessionIncrementalPersistenceState persistenceState)
    {
        if (!ReferenceEquals(snapshot.Analyses, persistenceState.PersistedAnalysesSource)
            || snapshot.Analyses.Count != persistenceState.PersistedAnalysisCount)
        {
            WriteJsonAtomic(
                layout.AnalysesFilePath,
                new SessionAnalysesBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    Analyses = snapshot.Analyses.ToArray()
                });
            persistenceState.PersistedAnalysisCount = snapshot.Analyses.Count;
            persistenceState.PersistedAnalysesSource = snapshot.Analyses;
        }

        if (!ReferenceEquals(snapshot.Annotations, persistenceState.PersistedAnnotationsSource))
        {
            WriteJsonAtomic(
                layout.AnnotationsFilePath,
                new SessionAnnotationsBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    Annotations = snapshot.Annotations.ToArray()
                });
            persistenceState.PersistedAnnotationsSource = snapshot.Annotations;
        }

        if (!ReferenceEquals(snapshot.AgentTaskLinks, persistenceState.PersistedAgentTaskLinksSource))
        {
            WriteJsonAtomic(
                layout.AgentTaskLinksFilePath,
                new SessionAgentTaskLinksBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    Tasks = snapshot.AgentTaskLinks.ToArray()
                });
            persistenceState.PersistedAgentTaskLinksSource = snapshot.AgentTaskLinks;
        }

        if (!ReferenceEquals(snapshot.Images, persistenceState.PersistedImagesSource))
        {
            var addedImages = snapshot.Images
                .Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId))
                .Where(frame => !persistenceState.PersistedImageFrameIds.Contains(frame.FrameId))
                .DistinctBy(frame => frame.FrameId, StringComparer.Ordinal)
                .ToArray();
            if (addedImages.Length > 0)
            {
                AppendJsonLine(
                    GetImagesAppendFilePath(layout),
                    new SessionImagesAppendDocument
                    {
                        SavedAtUtc = savedAtUtc,
                        Images = addedImages
                    });
                persistenceState.PersistedImageFrameIds.UnionWith(
                    addedImages.Select(frame => frame.FrameId));
            }

            persistenceState.PersistedImageCount = Math.Max(
                persistenceState.PersistedImageCount,
                persistenceState.PersistedImageFrameIds.Count);
            persistenceState.PersistedImagesSource = snapshot.Images;
        }

        if (!ReferenceEquals(snapshot.Touches, persistenceState.PersistedTouchesSource))
        {
            var touchCandidates = snapshot.Touches
                .Where(touch => !string.IsNullOrWhiteSpace(touch.Id))
                .GroupBy(touch => touch.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var addedTouches = touchCandidates
                .Where(touch => !persistenceState.PersistedTouchIds.Contains(touch.Id))
                .ToArray();

            if (addedTouches.Length > 0)
            {
                AppendJsonLine(
                    GetTouchesAppendFilePath(layout),
                    new SessionTouchesAppendDocument
                    {
                        SavedAtUtc = savedAtUtc,
                        Batches = SessionTouchPacking.Pack(addedTouches)
                    });
                persistenceState.PersistedTouchIds.UnionWith(
                    addedTouches.Select(touch => touch.Id));
            }

            persistenceState.PersistedTouchInputCount = Math.Max(
                persistenceState.PersistedTouchInputCount,
                persistenceState.PersistedTouchIds.Count);
            persistenceState.PersistedTouchesSource = snapshot.Touches;
        }

        if (!ReferenceEquals(snapshot.ApplicationEvents, persistenceState.PersistedApplicationEventsSource))
        {
            WriteJsonAtomic(
                layout.ApplicationEventsFilePath,
                new SessionApplicationEventsBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    Events = snapshot.ApplicationEvents.ToArray()
                });
            persistenceState.PersistedApplicationEventsSource = snapshot.ApplicationEvents;
        }

        if (!ReferenceEquals(snapshot.NetworkRequests, persistenceState.PersistedNetworkRequestsSource))
        {
            foreach (var request in snapshot.NetworkRequests
                         .Where(request => !persistenceState.PersistedNetworkRequestIds.Contains(request.Id)))
            {
                WriteNetworkRequestDocument(layout.NetworkRequestsDirectoryPath, request);
                persistenceState.PersistedNetworkRequestIds.Add(request.Id);
            }

            persistenceState.PersistedNetworkRequestsSource = snapshot.NetworkRequests;
        }

        if (!ReferenceEquals(snapshot.VisualTreeSnapshots, persistenceState.PersistedVisualTreeSnapshots.PersistedSource)
            || snapshot.VisualTreeSnapshots.Count != persistenceState.PersistedVisualTreeSnapshots.PersistedSnapshotCount)
        {
            ReconcileVisualTreeSnapshotDocuments(
                layout,
                savedAtUtc,
                snapshot.VisualTreeSnapshots,
                persistenceState.PersistedVisualTreeSnapshots);
            persistenceState.PersistedVisualTreeSnapshots.PersistedSource = snapshot.VisualTreeSnapshots;
            persistenceState.PersistedVisualTreeSnapshots.PersistedSnapshotCount = snapshot.VisualTreeSnapshots.Count;
        }

        if (!ReferenceEquals(snapshot.ArtifactSnapshots, persistenceState.PersistedArtifactSnapshots.PersistedSource)
            || snapshot.ArtifactSnapshots.Count != persistenceState.PersistedArtifactSnapshots.PersistedSnapshotCount)
        {
            ReconcileArtifactSnapshotDocuments(
                layout,
                savedAtUtc,
                snapshot.ArtifactSnapshots,
                persistenceState.PersistedArtifactSnapshots);
            persistenceState.PersistedArtifactSnapshots.PersistedSource = snapshot.ArtifactSnapshots;
            persistenceState.PersistedArtifactSnapshots.PersistedSnapshotCount = snapshot.ArtifactSnapshots.Count;
        }

        if (!ReferenceEquals(snapshot.MetricChannels, persistenceState.PersistedMetricChannelsSource))
        {
            WriteJsonAtomic(
                layout.MetricChannelsFilePath,
                new SessionMetricChannelsBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    Channels = snapshot.MetricChannels.ToArray()
                });
            persistenceState.PersistedMetricChannelsSource = snapshot.MetricChannels;
        }

        if (!ReferenceEquals(snapshot.DeviceProfile, persistenceState.PersistedDeviceProfileSource)
            || !string.Equals(snapshot.DeviceProfileJson, persistenceState.PersistedDeviceProfileJson, StringComparison.Ordinal))
        {
            WriteDeviceProfileBlob(layout.DeviceProfileFilePath, savedAtUtc, snapshot);
            persistenceState.PersistedDeviceProfileSource = snapshot.DeviceProfile;
            persistenceState.PersistedDeviceProfileJson = snapshot.DeviceProfileJson;
        }

        if (!string.Equals(
                snapshot.AppToolCatalog?.Schema,
                persistenceState.PersistedAppToolCatalogSchema,
                StringComparison.Ordinal)
            || snapshot.AppToolCatalog?.CapturedAtUtc
            != persistenceState.PersistedAppToolCatalogCapturedAtUtc)
        {
            WriteAppToolCatalogBlob(
                layout.AppToolCatalogFilePath,
                savedAtUtc,
                snapshot.AppToolCatalog);
            persistenceState.PersistedAppToolCatalogSchema = snapshot.AppToolCatalog?.Schema;
            persistenceState.PersistedAppToolCatalogCapturedAtUtc = snapshot.AppToolCatalog?.CapturedAtUtc;
        }
    }

    private static void WriteIncrementalTelemetrySegments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        AppSessionSnapshot snapshot,
        SessionIncrementalPersistenceState persistenceState)
    {
        var totalMetricSampleCount = Math.Max(snapshot.TotalMetricSampleCount, snapshot.Metrics.Count);
        if (totalMetricSampleCount <= persistenceState.PersistedMetricSampleCount)
        {
            if (totalMetricSampleCount == persistenceState.PersistedMetricSampleCount
                && snapshot.Metrics.Count == totalMetricSampleCount
                && !ReferenceEquals(snapshot.Metrics, persistenceState.PersistedMetricsSource))
            {
                var persistedMetrics = SessionSnapshotReader.LoadTelemetry(layout.TelemetryDirectoryPath);
                if (!AreMetricSamplesEquivalent(persistedMetrics, snapshot.Metrics))
                {
                    AppendJsonLine(
                        GetTelemetryAppendFilePath(layout),
                        new SessionTelemetryAppendDocument
                        {
                            SavedAtUtc = savedAtUtc,
                            ReplaceAll = true,
                            Metrics = snapshot.Metrics.ToArray()
                        });
                }
            }

            persistenceState.PersistedMetricsSource = snapshot.Metrics;
            return;
        }

        var retainedMetricStartIndex = Math.Max(0, totalMetricSampleCount - snapshot.Metrics.Count);
        var firstUnpersistedRetainedIndex = persistenceState.PersistedMetricSampleCount - retainedMetricStartIndex;
        if (firstUnpersistedRetainedIndex >= snapshot.Metrics.Count)
        {
            persistenceState.PersistedMetricsSource = snapshot.Metrics;
            return;
        }

        Directory.CreateDirectory(layout.TelemetryDirectoryPath);
        var firstMetricIndex = Math.Max(0, firstUnpersistedRetainedIndex);
        var addedMetrics = CopyRange(
            snapshot.Metrics,
            firstMetricIndex,
            snapshot.Metrics.Count - firstMetricIndex);
        if (addedMetrics.Length == 0)
        {
            persistenceState.PersistedMetricsSource = snapshot.Metrics;
            return;
        }

        AppendJsonLine(
            GetTelemetryAppendFilePath(layout),
            new SessionTelemetryAppendDocument
            {
                SavedAtUtc = savedAtUtc,
                Metrics = addedMetrics
            });

        persistenceState.PersistedMetricSampleCount = totalMetricSampleCount;
        persistenceState.PersistedMetricsSource = snapshot.Metrics;
    }

    private static bool AreMetricSamplesEquivalent(
        IReadOnlyList<SessionMetricSample> left,
        IReadOnlyList<SessionMetricSample> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var orderedLeft = OrderMetricSamples(left);
        var orderedRight = OrderMetricSamples(right);
        for (var index = 0; index < orderedLeft.Length; index++)
        {
            var leftSample = orderedLeft[index];
            var rightSample = orderedRight[index];
            if (leftSample.ChannelId != rightSample.ChannelId
                || leftSample.Value != rightSample.Value
                || leftSample.CapturedAtUtc != rightSample.CapturedAtUtc
                || leftSample.SegmentId != rightSample.SegmentId)
            {
                return false;
            }
        }

        return true;
    }

    private static SessionMetricSample[] OrderMetricSamples(IReadOnlyList<SessionMetricSample> samples)
        => samples
            .OrderBy(sample => sample.CapturedAtUtc)
            .ThenBy(sample => sample.ChannelId)
            .ThenBy(sample => sample.SegmentId)
            .ThenBy(sample => sample.Value)
            .ToArray();

    private static T[] CopyRange<T>(IReadOnlyList<T> source, int startIndex, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<T>();
        }

        var items = new T[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = source[startIndex + i];
        }

        return items;
    }

    private static void DeleteLogSegments(SessionPathLayout layout)
    {
        TryDeleteDirectory(layout.LogSegmentsDirectoryPath);
        DeleteIfExists(GetLogsAppendFilePath(layout));
    }

    private long ResolveCacheSizeBytesForSnapshot(AppSessionSnapshot snapshot, SessionPathLayout layout)
    {
        if (IsRecordingStatus(snapshot.Status))
        {
            return Math.Max(0, snapshot.CacheSizeBytes);
        }

        return GetDirectorySize(layout.SessionDirectoryPath);
    }

    private static bool IsRecordingStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var normalizedStatus = status.Trim();
        return string.Equals(normalizedStatus, "Connected", StringComparison.OrdinalIgnoreCase)
               || normalizedStatus.Contains("open", StringComparison.OrdinalIgnoreCase);
    }

    private static void PruneDetachedSessionContent(SessionPathLayout layout, AppSessionSnapshot snapshot)
    {
        PruneDetachedCapturedImages(layout, snapshot.Images);
        PruneDetachedArtifactPayloadDirectories(layout, snapshot.ArtifactSnapshots);
        ReconcileNetworkRequestDocuments(layout.NetworkRequestsDirectoryPath, snapshot.NetworkRequests);
    }

    private static void ReconcileNetworkRequestDocuments(
        string directoryPath,
        IReadOnlyList<SessionNetworkRequest> requests)
    {
        Directory.CreateDirectory(directoryPath);
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests
                     .Select(SessionNetworkRequestSanitizer.Normalize)
                     .Where(static request => request is not null)
                     .Cast<SessionNetworkRequest>()
                     .GroupBy(static request => request.Id, StringComparer.Ordinal)
                     .Select(static group => group.Last()))
        {
            expectedPaths.Add(Path.GetFullPath(WriteNetworkRequestDocument(directoryPath, request)));
        }

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!expectedPaths.Contains(Path.GetFullPath(filePath)))
            {
                TryDeleteFile(filePath);
            }
        }
    }

    private static string WriteNetworkRequestDocument(
        string directoryPath,
        SessionNetworkRequest request)
    {
        var normalized = SessionNetworkRequestSanitizer.Normalize(request)
                         ?? throw new InvalidDataException("Network request record is invalid.");
        Directory.CreateDirectory(directoryPath);
        var fileName = $"{normalized.StartedAtUtc:yyyyMMddHHmmssfff}-{FileNameUtil.Sanitize(normalized.Id)}.json";
        var filePath = Path.Combine(directoryPath, fileName);
        WriteJsonAtomic(filePath, normalized);
        return filePath;
    }

    private static void PruneDetachedCapturedImages(
        SessionPathLayout layout,
        IReadOnlyList<SessionImageFrame> images)
    {
        if (!Directory.Exists(layout.CapturedImagesDirectoryPath))
        {
            return;
        }

        var expectedFilePaths = images
            .Select(frame => Path.GetFullPath(SessionImageArtifactPath.ResolveCapturedImagePath(layout.CapturedImagesDirectoryPath, frame)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(layout.CapturedImagesDirectoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!expectedFilePaths.Contains(Path.GetFullPath(filePath)))
            {
                TryDeleteFile(filePath);
            }
        }
    }

    private static void PruneDetachedArtifactPayloadDirectories(
        SessionPathLayout layout,
        IReadOnlyList<SessionArtifactSnapshot> artifactSnapshots)
    {
        if (!Directory.Exists(layout.ArtifactsDirectoryPath))
        {
            return;
        }

        var expectedDirectoryPaths = artifactSnapshots
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.ArtifactDirectoryName))
            .Select(snapshot => Path.GetFullPath(Path.Combine(
                layout.ArtifactsDirectoryPath,
                FileNameUtil.Sanitize(snapshot.ArtifactDirectoryName))))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var directoryPath in Directory.EnumerateDirectories(layout.ArtifactsDirectoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!expectedDirectoryPaths.Contains(Path.GetFullPath(directoryPath)))
            {
                TryDeleteDirectory(directoryPath);
            }
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            DeleteIfExists(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteDeviceProfileBlob(string filePath, DateTimeOffset savedAtUtc, AppSessionSnapshot snapshot)
    {
        var profileJson = NormalizeJson(snapshot.DeviceProfileJson);
        if (string.IsNullOrWhiteSpace(profileJson) && snapshot.DeviceProfile is not null)
        {
            profileJson = JsonSerializer.Serialize(snapshot.DeviceProfile, JsonUtil.Compact);
        }

        if (string.IsNullOrWhiteSpace(profileJson))
        {
            DeleteIfExists(filePath);
            return;
        }

        WriteJsonAtomic(
            filePath,
            new SessionDeviceProfileBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                ProfileJson = profileJson
            });
    }

    private static void WriteAppToolCatalogBlob(
        string filePath,
        DateTimeOffset savedAtUtc,
        SessionAppToolCatalogSnapshot? catalog)
    {
        if (catalog is null)
        {
            DeleteIfExists(filePath);
            return;
        }

        WriteJsonAtomic(
            filePath,
            new SessionAppToolCatalogBlobDocument
            {
                SavedAtUtc = savedAtUtc,
                Catalog = SessionSnapshotCloner.CloneAppToolCatalog(catalog)!
            });
    }

    private static void WriteTelemetryBlobs(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionMetricChannel> metricChannels,
        IReadOnlyList<SessionMetricSample> metrics)
    {
        Directory.CreateDirectory(layout.TelemetryDirectoryPath);

        var channelMap = metricChannels.ToDictionary(channel => channel.ChannelId);
        var expectedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grouping in metrics
                     .GroupBy(metric => metric.ChannelId)
                     .OrderBy(group => group.Key))
        {
            var telemetryFilePath = GetTelemetryBlobFilePath(layout, grouping.Key);
            expectedFilePaths.Add(telemetryFilePath);

            channelMap.TryGetValue(grouping.Key, out var channel);
            WriteJsonAtomic(
                telemetryFilePath,
                new SessionTelemetryBlobDocument
                {
                    SavedAtUtc = savedAtUtc,
                    ChannelId = grouping.Key,
                    ChannelName = channel?.Name,
                    TelemetryType = ResolveTelemetryType(grouping.Key, channel),
                    Metrics = grouping
                        .OrderBy(metric => metric.CapturedAtUtc)
                        .ToArray()
                });
        }

        foreach (var existingFilePath in Directory.EnumerateFiles(layout.TelemetryDirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!expectedFilePaths.Contains(existingFilePath))
            {
                File.Delete(existingFilePath);
            }
        }

        DeleteIfExists(GetTelemetryAppendFilePath(layout));
    }

    private sealed record SessionPersistenceCounts(
        int LogCount,
        int ImageCount,
        int TouchInputCount,
        int MetricSampleCount);

    private static void AppendJsonLine<T>(string filePath, T value)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.None);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Position = stream.Length;
        JsonSerializer.Serialize(stream, value, JsonUtil.Compact);
        stream.WriteByte((byte)'\n');
    }

    private SessionImageFrame? SaveSessionImage(
        string appId,
        string sessionId,
        DateTimeOffset capturedAtUtc,
        string format,
        int width,
        int height,
        int quality,
        ReadOnlyMemory<byte> bytes,
        bool allowDuplicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        var layout = GetSessionLayout(appId, sessionId);
        Directory.CreateDirectory(layout.CapturedImagesDirectoryPath);

        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (bytes.Length == 0)
        {
            return null;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes.Span));

        lock (artifactGate)
        {
            if (!allowDuplicate
                && lastArtifactBySessionId.TryGetValue(sessionId, out var lastArtifact)
                && string.Equals(lastArtifact.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var frameId = $"{capturedAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var frame = new SessionImageFrame
        {
            FrameId = frameId,
            CapturedAtUtc = capturedAtUtc,
            Format = normalizedFormat,
            Width = width,
            Height = height,
            Quality = quality,
            ByteCount = bytes.Length
        };
        var filePath = SessionImageArtifactPath.ResolveCapturedImagePath(layout.CapturedImagesDirectoryPath, frame);
        WriteAllBytesAtomic(filePath, bytes.Span);

        lock (artifactGate)
        {
            lastArtifactBySessionId[sessionId] = new SessionImageArtifact(fingerprint);
        }

        return frame;
    }
}
