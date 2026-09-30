namespace Ansight.Host.Sessions;

using Ansight.Host;
using Ansight.Pairing.Models;
using System.Globalization;
using System.Text.Json.Nodes;
using static Ansight.Host.Runtime.State.RuntimeSnapshotNormalizer;

internal static class SessionTimelineTransformer
{
    internal static AppSessionSnapshot CreateExtractionBaseSnapshot(
        AppSessionSnapshot sourceSnapshot,
        string extractedSessionId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        string? name,
        SessionAnnotation? sourceAnnotation)
    {
        var logStreams = SessionLogStreams.Normalize(sourceSnapshot.LogStreams, sourceSnapshot.Logs);
        return new AppSessionSnapshot
        {
            SessionId = extractedSessionId,
            AppId = sourceSnapshot.AppId,
            ClientName = sourceSnapshot.ClientName,
            RemoteAddress = sourceSnapshot.RemoteAddress,
            Name = NormalizeSessionName(name) ?? BuildDefaultExtractionName(sourceSnapshot, rangeStartUtc, rangeEndUtc, sourceAnnotation),
            CreatedUtc = sourceSnapshot.CreatedUtc,
            ConfigId = sourceSnapshot.ConfigId,
            ProcessSessionId = null,
            Status = "Extracted",
            LastUpdatedUtc = sourceSnapshot.LastUpdatedUtc,
            IsHistorical = true,
            CacheSizeBytes = 0,
            IsPinned = sourceSnapshot.IsPinned,
            Author = sourceSnapshot.Author,
            ReplaySource = CreateExtractionReplaySource(sourceSnapshot, rangeStartUtc, rangeEndUtc, sourceAnnotation),
            CaptureSource = sourceSnapshot.CaptureSource,
            SdkVersion = sourceSnapshot.SdkVersion,
            Tags = sourceSnapshot.Tags.ToArray(),
            Notes = sourceSnapshot.Notes,
            CustomProperties = SessionSnapshotCloner.CloneCustomProperties(sourceSnapshot.CustomProperties),
            AppState = sourceSnapshot.AppState,
            AppStateChangedUtc = sourceSnapshot.AppStateChangedUtc,
            DeviceProfile = sourceSnapshot.DeviceProfile,
            DeviceProfileJson = sourceSnapshot.DeviceProfileJson,
            AppIcon = sourceSnapshot.AppIcon,
            AppToolCatalog = SessionSnapshotCloner.CloneAppToolCatalog(sourceSnapshot.AppToolCatalog),
            Analyses = Array.Empty<SessionAnalysisRecord>(),
            Annotations = sourceSnapshot.Annotations.ToArray(),
            Images = sourceSnapshot.Images.ToArray(),
            Touches = sourceSnapshot.Touches.ToArray(),
            NetworkRequests = sourceSnapshot.NetworkRequests
                .Where(request => request.StartedAtUtc <= rangeEndUtc
                                  && request.CompletedAtUtc >= rangeStartUtc)
                .ToArray(),
            VisualTreeSnapshots = sourceSnapshot.VisualTreeSnapshots.ToArray(),
            ArtifactSnapshots = sourceSnapshot.ArtifactSnapshots.ToArray(),
            ApplicationEvents = sourceSnapshot.ApplicationEvents
                .Where(appEvent => appEvent.CapturedAtUtc >= rangeStartUtc
                                   && appEvent.CapturedAtUtc <= rangeEndUtc)
                .ToArray(),
            LogStreams = logStreams,
            Logs = SessionLogStreams.Flatten(logStreams),
            MetricChannels = sourceSnapshot.MetricChannels.ToArray(),
            Metrics = sourceSnapshot.Metrics.ToArray(),
            TotalApplicationEventCount = sourceSnapshot.ApplicationEvents.Count(appEvent =>
                appEvent.CapturedAtUtc >= rangeStartUtc && appEvent.CapturedAtUtc <= rangeEndUtc),
            TotalNetworkRequestCount = sourceSnapshot.NetworkRequests.Count(request =>
                request.StartedAtUtc <= rangeEndUtc && request.CompletedAtUtc >= rangeStartUtc)
        };
    }

    private static SessionReplaySource CreateExtractionReplaySource(
        AppSessionSnapshot sourceSnapshot,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionAnnotation? sourceAnnotation)
    {
        var detail = sourceAnnotation is null
            ? $"Source session {sourceSnapshot.SessionId}, range {FormatExtractionTimestamp(rangeStartUtc)} to {FormatExtractionTimestamp(rangeEndUtc)}."
            : $"Source session {sourceSnapshot.SessionId}, annotation {sourceAnnotation.AnnotationId}, range {FormatExtractionTimestamp(rangeStartUtc)} to {FormatExtractionTimestamp(rangeEndUtc)}.";
        return new SessionReplaySource
        {
            Kind = "extract",
            DisplayName = "Extracted session",
            Detail = detail,
            SourceId = sourceSnapshot.SessionId
        };
    }

    private static string BuildDefaultExtractionName(
        AppSessionSnapshot sourceSnapshot,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionAnnotation? sourceAnnotation)
    {
        var sourceName = NormalizeSessionName(sourceSnapshot.Name) ?? sourceSnapshot.SessionId;
        var sourceDescription = sourceAnnotation is null
            ? "slice"
            : $"annotation {NormalizeSessionName(sourceAnnotation.Label) ?? sourceAnnotation.AnnotationId}";
        return $"{sourceName} {sourceDescription} {FormatExtractionTimeOfDay(rangeStartUtc)}-{FormatExtractionTimeOfDay(rangeEndUtc)}";
    }

    private static string FormatExtractionTimestamp(DateTimeOffset timestampUtc)
        => timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatExtractionTimeOfDay(DateTimeOffset timestampUtc)
        => timestampUtc.ToUniversalTime().ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static bool TryResolveAnnotationExtractionRange(
        SessionAnnotation annotation,
        out DateTimeOffset rangeStartUtc,
        out DateTimeOffset rangeEndUtc)
    {
        rangeStartUtc = annotation.StartUtc.ToUniversalTime();
        rangeEndUtc = (annotation.EndUtc ?? annotation.StartUtc).ToUniversalTime();
        foreach (var geometry in annotation.Geometry)
        {
            var capturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime();
            if (capturedAtUtc < rangeStartUtc)
            {
                rangeStartUtc = capturedAtUtc;
            }

            if (capturedAtUtc > rangeEndUtc)
            {
                rangeEndUtc = capturedAtUtc;
            }
        }

        if (rangeEndUtc < rangeStartUtc)
        {
            (rangeStartUtc, rangeEndUtc) = (rangeEndUtc, rangeStartUtc);
        }

        return rangeEndUtc > rangeStartUtc;
    }

    internal static bool TryNormalizeTimelineTrimRange(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        out DateTimeOffset rangeStartUtc,
        out DateTimeOffset rangeEndUtc)
    {
        rangeStartUtc = startUtc.ToUniversalTime();
        rangeEndUtc = endUtc.ToUniversalTime();
        if (rangeEndUtc < rangeStartUtc)
        {
            (rangeStartUtc, rangeEndUtc) = (rangeEndUtc, rangeStartUtc);
        }

        return rangeEndUtc > rangeStartUtc;
    }

    internal static bool ApplyTimelineTrim(
        SessionState session,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null)
    {
        var recordingStartUtc = session.CreatedUtc.ToUniversalTime();
        var recordingEndUtc = session.LastUpdatedUtc.ToUniversalTime();
        if (recordingEndUtc < recordingStartUtc)
        {
            recordingEndUtc = recordingStartUtc;
        }

        var changed = false;
        changed |= TrimTimestampedItems(session.Logs, log => log.TimestampUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering logs…", report);
        changed |= TrimTimestampedItems(session.Images, frame => frame.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering screenshots…", report);
        changed |= TrimTimestampedItems(session.Touches, touch => touch.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering touches…", report);
        changed |= TrimTimestampedItems(session.VisualTreeSnapshots, snapshot => snapshot.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering visual trees…", report);
        changed |= TrimTimestampedItems(session.ArtifactSnapshots, snapshot => snapshot.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering artifacts…", report);
        changed |= TrimTimestampedItems(session.ApplicationEvents, appEvent => appEvent.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering application events…", report);
        changed |= TrimTimestampedItems(session.Metrics, sample => sample.CapturedAtUtc, rangeStartUtc, rangeEndUtc, mode, "Filtering telemetry…", report);
        report?.Invoke(new("Updating annotations and timeline bounds…"));
        changed |= TrimAnnotations(session.Annotations, rangeStartUtc, rangeEndUtc, mode);
        changed |= TrimAppStateChangedTimestamp(session, rangeStartUtc, rangeEndUtc, mode);
        changed |= mode == SessionTimelineTrimMode.CutSelection
            ? ApplyTimelineCutCompaction(session, recordingStartUtc, recordingEndUtc, rangeStartUtc, rangeEndUtc)
            : ApplyTimelineKeepSelectionBounds(session, rangeStartUtc, rangeEndUtc);

        if (session.Analyses.Count > 0)
        {
            session.Analyses.Clear();
            changed = true;
        }

        if (changed)
        {
            report?.Invoke(new("Rebuilding evidence indexes…"));
            RebuildTimelineDeduplicationState(session);
        }

        return changed;
    }

    private static bool ApplyTimelineKeepSelectionBounds(
        SessionState session,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        var changed = false;
        if (session.CreatedUtc.ToUniversalTime() != rangeStartUtc)
        {
            session.CreatedUtc = rangeStartUtc;
            changed = true;
        }

        if (session.LastUpdatedUtc.ToUniversalTime() != rangeEndUtc)
        {
            session.LastUpdatedUtc = rangeEndUtc;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyTimelineCutCompaction(
        SessionState session,
        DateTimeOffset recordingStartUtc,
        DateTimeOffset recordingEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        var effectiveStartUtc = MaxTimestamp(recordingStartUtc, rangeStartUtc);
        var effectiveEndUtc = MinTimestamp(recordingEndUtc, rangeEndUtc);
        if (effectiveEndUtc <= effectiveStartUtc)
        {
            return false;
        }

        var removedDuration = effectiveEndUtc - effectiveStartUtc;
        var changed = ShiftSessionTimelineAfterCut(session, rangeEndUtc, removedDuration);
        var compactedEndUtc = recordingEndUtc > rangeEndUtc
            ? recordingEndUtc - removedDuration
            : effectiveStartUtc;
        if (compactedEndUtc < recordingStartUtc)
        {
            compactedEndUtc = recordingStartUtc;
        }

        if (session.CreatedUtc.ToUniversalTime() != recordingStartUtc)
        {
            session.CreatedUtc = recordingStartUtc;
            changed = true;
        }

        if (session.LastUpdatedUtc.ToUniversalTime() != compactedEndUtc)
        {
            session.LastUpdatedUtc = compactedEndUtc;
            changed = true;
        }

        return changed;
    }

    private static bool ShiftSessionTimelineAfterCut(
        SessionState session,
        DateTimeOffset cutoffUtc,
        TimeSpan offset)
    {
        var changed = false;
        changed |= ShiftTimestampedItemsAfterCut(
            session.Logs,
            log => log.TimestampUtc,
            (log, shiftedTimestampUtc) => log with { TimestampUtc = shiftedTimestampUtc },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.Images,
            frame => frame.CapturedAtUtc,
            (frame, shiftedCapturedAtUtc) => new SessionImageFrame
            {
                FrameId = frame.FrameId,
                CapturedAtUtc = shiftedCapturedAtUtc,
                Format = frame.Format,
                Width = frame.Width,
                Height = frame.Height,
                Quality = frame.Quality,
                ByteCount = frame.ByteCount
            },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.Touches,
            touch => touch.CapturedAtUtc,
            (touch, shiftedCapturedAtUtc) => new SessionTouchInputRecord
            {
                Id = touch.Id,
                Action = touch.Action,
                CapturedAtUtc = shiftedCapturedAtUtc,
                PointerId = touch.PointerId,
                PointerIndex = touch.PointerIndex,
                PointerCount = touch.PointerCount,
                X = touch.X,
                Y = touch.Y,
                NormalizedX = touch.NormalizedX,
                NormalizedY = touch.NormalizedY,
                SurfaceWidth = touch.SurfaceWidth,
                SurfaceHeight = touch.SurfaceHeight,
                CoordinateSpace = touch.CoordinateSpace,
                CoordinateUnit = touch.CoordinateUnit,
                SurfaceScale = touch.SurfaceScale
            },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.VisualTreeSnapshots,
            snapshot => snapshot.CapturedAtUtc,
            (snapshot, shiftedCapturedAtUtc) => new SessionVisualTreeSnapshot
            {
                SnapshotId = snapshot.SnapshotId,
                CapturedAtUtc = shiftedCapturedAtUtc,
                VisualTreeKind = snapshot.VisualTreeKind,
                VisualTreeFormat = snapshot.VisualTreeFormat,
                RuntimePlatform = snapshot.RuntimePlatform,
                Source = snapshot.Source,
                RootScope = snapshot.RootScope,
                MaxDepth = snapshot.MaxDepth,
                IncludeProperties = snapshot.IncludeProperties,
                IncludeBindableProperties = snapshot.IncludeBindableProperties,
                NodeCount = snapshot.NodeCount,
                Truncated = snapshot.Truncated,
                ScreenshotFrameId = snapshot.ScreenshotFrameId,
                ScreenshotCapturedAtUtc = ShiftTimestampAfterCut(snapshot.ScreenshotCapturedAtUtc, cutoffUtc, offset),
                ActionId = snapshot.ActionId,
                ActionCapability = snapshot.ActionCapability,
                EvidencePhase = snapshot.EvidencePhase,
                TreeHash = snapshot.TreeHash,
                ScreenshotHash = snapshot.ScreenshotHash,
                Payload = snapshot.Payload.DeepClone() as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject()
            },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.ArtifactSnapshots,
            snapshot => snapshot.CapturedAtUtc,
            (snapshot, shiftedCapturedAtUtc) => new SessionArtifactSnapshot
            {
                SnapshotId = snapshot.SnapshotId,
                CapturedAtUtc = shiftedCapturedAtUtc,
                Source = snapshot.Source,
                RootAlias = snapshot.RootAlias,
                RootPath = snapshot.RootPath,
                RelativePath = snapshot.RelativePath,
                Name = snapshot.Name,
                Kind = snapshot.Kind,
                ArtifactDirectoryName = snapshot.ArtifactDirectoryName,
                DirectoryCount = snapshot.DirectoryCount,
                FileCount = snapshot.FileCount,
                ByteCount = snapshot.ByteCount,
                Truncated = snapshot.Truncated,
                Entries = snapshot.Entries
                    .Select(SessionSnapshotCloner.CloneArtifactEntry)
                    .ToArray()
            },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.ApplicationEvents,
            appEvent => appEvent.CapturedAtUtc,
            (appEvent, shiftedCapturedAtUtc) => appEvent with { CapturedAtUtc = shiftedCapturedAtUtc },
            cutoffUtc,
            offset);
        changed |= ShiftTimestampedItemsAfterCut(
            session.Metrics,
            sample => sample.CapturedAtUtc,
            (sample, shiftedCapturedAtUtc) => new SessionMetricSample
            {
                ChannelId = sample.ChannelId,
                Value = sample.Value,
                CapturedAtUtc = shiftedCapturedAtUtc,
                SegmentId = sample.SegmentId
            },
            cutoffUtc,
            offset);
        changed |= ShiftAnnotationsAfterCut(session.Annotations, cutoffUtc, offset);
        var shiftedAppStateChangedUtc = ShiftTimestampAfterCut(session.AppStateChangedUtc, cutoffUtc, offset);
        if (shiftedAppStateChangedUtc != session.AppStateChangedUtc?.ToUniversalTime())
        {
            session.AppStateChangedUtc = shiftedAppStateChangedUtc;
            changed = true;
        }

        return changed;
    }

    private static bool TrimAppStateChangedTimestamp(
        SessionState session,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionTimelineTrimMode mode)
    {
        if (!session.AppStateChangedUtc.HasValue)
        {
            return false;
        }

        var isInRange = IsInTimelineTrimRange(
            session.AppStateChangedUtc.Value,
            rangeStartUtc,
            rangeEndUtc);
        var shouldRemove = mode == SessionTimelineTrimMode.CutSelection
            ? isInRange
            : !isInRange;
        if (!shouldRemove)
        {
            return false;
        }

        session.AppStateChangedUtc = null;
        return true;
    }

    private static bool ShiftTimestampedItemsAfterCut<T>(
        List<T> items,
        Func<T, DateTimeOffset> timestampSelector,
        Func<T, DateTimeOffset, T> shiftedItemFactory,
        DateTimeOffset cutoffUtc,
        TimeSpan offset)
    {
        var changed = false;
        for (var index = 0; index < items.Count; index++)
        {
            var timestampUtc = timestampSelector(items[index]).ToUniversalTime();
            if (timestampUtc <= cutoffUtc)
            {
                continue;
            }

            items[index] = shiftedItemFactory(items[index], timestampUtc - offset);
            changed = true;
        }

        return changed;
    }

    private static bool ShiftAnnotationsAfterCut(
        List<SessionAnnotation> annotations,
        DateTimeOffset cutoffUtc,
        TimeSpan offset)
    {
        var changed = false;
        for (var index = 0; index < annotations.Count; index++)
        {
            var annotation = annotations[index];
            var shiftedStartUtc = ShiftTimestampAfterCut(annotation.StartUtc, cutoffUtc, offset);
            var shiftedEndUtc = ShiftTimestampAfterCut(annotation.EndUtc, cutoffUtc, offset);
            if (shiftedStartUtc == annotation.StartUtc.ToUniversalTime()
                && shiftedEndUtc == annotation.EndUtc?.ToUniversalTime()
                && annotation.Geometry.All(geometry => ShiftTimestampAfterCut(geometry.CapturedAtUtc, cutoffUtc, offset) == geometry.CapturedAtUtc.ToUniversalTime()))
            {
                continue;
            }

            annotations[index] = new SessionAnnotation
            {
                AnnotationId = annotation.AnnotationId,
                StartUtc = shiftedStartUtc,
                EndUtc = shiftedEndUtc,
                Label = annotation.Label,
                Source = annotation.Source,
                Notes = annotation.Notes,
                CaptureGroupId = annotation.CaptureGroupId,
                CustomData = annotation.CustomData?.DeepClone() as JsonObject,
                Evidence = annotation.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
                HookFailures = annotation.HookFailures.ToArray(),
                Geometry = annotation.Geometry
                    .Select(geometry => new SessionAnnotationGeometry
                    {
                        GeometryId = geometry.GeometryId,
                        FrameId = geometry.FrameId,
                        CapturedAtUtc = ShiftTimestampAfterCut(geometry.CapturedAtUtc, cutoffUtc, offset),
                        Kind = geometry.Kind,
                        X = geometry.X,
                        Y = geometry.Y,
                        Width = geometry.Width,
                        Height = geometry.Height,
                        Points = geometry.Points.Select(SessionSnapshotCloner.CloneAnnotationGeometryPoint).ToArray(),
                        Text = geometry.Text,
                        StrokeColor = geometry.StrokeColor,
                        StrokeWidth = geometry.StrokeWidth
                    })
                    .ToArray(),
                Target = SessionSnapshotCloner.CloneAnnotationTarget(annotation.Target)
            };
            changed = true;
        }

        return changed;
    }

    private static DateTimeOffset ShiftTimestampAfterCut(
        DateTimeOffset timestampUtc,
        DateTimeOffset cutoffUtc,
        TimeSpan offset)
    {
        var normalizedTimestampUtc = timestampUtc.ToUniversalTime();
        return normalizedTimestampUtc > cutoffUtc
            ? normalizedTimestampUtc - offset
            : normalizedTimestampUtc;
    }

    private static DateTimeOffset? ShiftTimestampAfterCut(
        DateTimeOffset? timestampUtc,
        DateTimeOffset cutoffUtc,
        TimeSpan offset)
    {
        return timestampUtc.HasValue
            ? ShiftTimestampAfterCut(timestampUtc.Value, cutoffUtc, offset)
            : null;
    }

    private static DateTimeOffset MaxTimestamp(DateTimeOffset leftUtc, DateTimeOffset rightUtc)
    {
        return leftUtc >= rightUtc ? leftUtc : rightUtc;
    }

    private static DateTimeOffset MinTimestamp(DateTimeOffset leftUtc, DateTimeOffset rightUtc)
    {
        return leftUtc <= rightUtc ? leftUtc : rightUtc;
    }

    private static bool TrimTimestampedItems<T>(
        List<T> items,
        Func<T, DateTimeOffset> timestampSelector,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionTimelineTrimMode mode,
        string message,
        Action<SessionTimelineTrimProgress>? report)
    {
        var beforeCount = items.Count;
        var processed = 0;
        report?.Invoke(new(message, 0, beforeCount));
        items.RemoveAll(item =>
        {
            processed++;
            if (processed % 10_000 == 0)
            {
                report?.Invoke(new(message, processed, beforeCount));
            }
            var isInRange = IsInTimelineTrimRange(timestampSelector(item), rangeStartUtc, rangeEndUtc);
            return mode == SessionTimelineTrimMode.CutSelection
                ? isInRange
                : !isInRange;
        });
        report?.Invoke(new(message, beforeCount, beforeCount));
        return items.Count != beforeCount;
    }

    private static bool TrimAnnotations(
        List<SessionAnnotation> annotations,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SessionTimelineTrimMode mode)
    {
        var trimmedAnnotations = mode == SessionTimelineTrimMode.CutSelection
            ? annotations
                .Where(annotation => !AnnotationOverlapsTimelineTrimRange(annotation, rangeStartUtc, rangeEndUtc))
                .Select(SessionSnapshotCloner.CloneAnnotation)
                .ToArray()
            : annotations
                .Select(annotation => TrimAnnotationToRange(annotation, rangeStartUtc, rangeEndUtc))
                .Where(annotation => annotation is not null)
                .Select(annotation => annotation!)
                .ToArray();

        Array.Sort(trimmedAnnotations, CompareAnnotations);
        if (AnnotationListsEqual(annotations, trimmedAnnotations))
        {
            return false;
        }

        annotations.Clear();
        annotations.AddRange(trimmedAnnotations);
        return true;
    }

    private static SessionAnnotation? TrimAnnotationToRange(
        SessionAnnotation annotation,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        if (!AnnotationOverlapsTimelineTrimRange(annotation, rangeStartUtc, rangeEndUtc))
        {
            return null;
        }

        var annotationStartUtc = annotation.StartUtc.ToUniversalTime();
        var annotationEndUtc = (annotation.EndUtc ?? annotation.StartUtc).ToUniversalTime();
        if (annotationEndUtc < annotationStartUtc)
        {
            (annotationStartUtc, annotationEndUtc) = (annotationEndUtc, annotationStartUtc);
        }

        var trimmedStartUtc = annotationStartUtc < rangeStartUtc ? rangeStartUtc : annotationStartUtc;
        var trimmedEndUtc = annotationEndUtc > rangeEndUtc ? rangeEndUtc : annotationEndUtc;
        var geometry = annotation.Geometry
            .Where(geometry => IsInTimelineTrimRange(geometry.CapturedAtUtc, rangeStartUtc, rangeEndUtc))
            .Select(geometry => new SessionAnnotationGeometry
            {
                GeometryId = geometry.GeometryId,
                FrameId = geometry.FrameId,
                CapturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime(),
                Kind = geometry.Kind,
                X = geometry.X,
                Y = geometry.Y,
                Width = geometry.Width,
                Height = geometry.Height,
                Points = geometry.Points.Select(SessionSnapshotCloner.CloneAnnotationGeometryPoint).ToArray(),
                Text = geometry.Text,
                StrokeColor = geometry.StrokeColor,
                StrokeWidth = geometry.StrokeWidth
            })
            .ToArray();

        return new SessionAnnotation
        {
            AnnotationId = annotation.AnnotationId,
            StartUtc = trimmedStartUtc,
            EndUtc = trimmedEndUtc > trimmedStartUtc ? trimmedEndUtc : null,
            Label = annotation.Label,
            Source = annotation.Source,
            Notes = annotation.Notes,
            CaptureGroupId = annotation.CaptureGroupId,
            CustomData = annotation.CustomData?.DeepClone() as JsonObject,
            Evidence = annotation.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
            HookFailures = annotation.HookFailures.ToArray(),
            Geometry = geometry,
            Target = SessionSnapshotCloner.CloneAnnotationTarget(annotation.Target)
        };
    }

    private static bool AnnotationOverlapsTimelineTrimRange(
        SessionAnnotation annotation,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        var annotationStartUtc = annotation.StartUtc.ToUniversalTime();
        var annotationEndUtc = (annotation.EndUtc ?? annotation.StartUtc).ToUniversalTime();
        if (annotationEndUtc < annotationStartUtc)
        {
            (annotationStartUtc, annotationEndUtc) = (annotationEndUtc, annotationStartUtc);
        }

        return annotationStartUtc <= rangeEndUtc && annotationEndUtc >= rangeStartUtc;
    }

    private static bool IsInTimelineTrimRange(
        DateTimeOffset timestampUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        var normalizedTimestampUtc = timestampUtc.ToUniversalTime();
        return normalizedTimestampUtc >= rangeStartUtc && normalizedTimestampUtc <= rangeEndUtc;
    }

    private static bool AnnotationListsEqual(
        IReadOnlyList<SessionAnnotation> left,
        IReadOnlyList<SessionAnnotation> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!SessionAnnotationsEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void RebuildTimelineDeduplicationState(SessionState session)
    {
        session.SeenEventIdsByStream.Clear();
        foreach (var stream in session.LogStreams.Values)
        {
            stream.Entries.Clear();
            stream.TotalEntryCount = 0;
        }

        foreach (var streamGroup in session.Logs.GroupBy(log => log.StreamId, StringComparer.Ordinal))
        {
            var stream = GetOrCreateLogStream(session, streamGroup.Key);
            stream.Entries.AddRange(streamGroup);
            stream.TotalEntryCount = stream.Entries.Count;
            var seenEventIds = session.GetSeenEventIds(streamGroup.Key);
            foreach (var eventId in streamGroup
                         .Select(log => log.EventId)
                         .Where(eventId => !string.IsNullOrWhiteSpace(eventId)))
            {
                seenEventIds.Add(eventId!);
            }
        }

        session.TotalLogCount = session.Logs.Count;

        session.SeenMetricKeys.Clear();
        foreach (var metric in session.Metrics)
        {
            session.SeenMetricKeys.Add(new MetricDeduplicationKey(metric.ChannelId, metric.Value, metric.CapturedAtUtc));
        }

        session.SeenTouchIds.Clear();
        foreach (var touchId in session.Touches
                     .Select(touch => touch.Id)
                     .Where(touchId => !string.IsNullOrWhiteSpace(touchId)))
        {
            session.SeenTouchIds.Add(touchId);
        }

        var seenApplicationEventIds = session.GetSeenEventIds("application-events");
        foreach (var eventId in session.ApplicationEvents
                     .Select(static appEvent => appEvent.EventId)
                     .Where(static eventId => !string.IsNullOrWhiteSpace(eventId)))
        {
            seenApplicationEventIds.Add(eventId);
        }
    }

    private static string? NormalizeSessionName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static SessionLogStreamState GetOrCreateLogStream(SessionState session, string streamId)
    {
        if (session.LogStreams.TryGetValue(streamId, out var stream))
        {
            return stream;
        }

        stream = new SessionLogStreamState
        {
            StreamId = streamId,
            Kind = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => SessionLogStreamKinds.AnsightSdk,
                SessionLogStreamIds.AndroidLogcat => SessionLogStreamKinds.AndroidLogcat,
                SessionLogStreamIds.AppleUnifiedLog => SessionLogStreamKinds.AppleUnifiedLog,
                _ => SessionLogStreamKinds.Imported
            },
            DisplayName = streamId switch
            {
                SessionLogStreamIds.AnsightSdk => "Ansight SDK",
                SessionLogStreamIds.AndroidLogcat => "Android Logcat",
                SessionLogStreamIds.AppleUnifiedLog => "Apple Unified Log",
                _ => streamId
            },
            Status = SessionLogStreamStatuses.Pending
        };
        session.LogStreams[streamId] = stream;
        return stream;
    }
}
