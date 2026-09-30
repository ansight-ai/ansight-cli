namespace Ansight.Host.Runtime.State;

using Ansight.Host;
using Ansight.Pairing.Models;
using System.Globalization;
using System.Text.Json.Nodes;
using static RuntimeSnapshotNormalizer;

internal sealed partial class RuntimeState
{
    public SessionImportResult ImportSessionSnapshot(
        AppSessionSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> imageBytesByFrameId,
        byte[]? appIconBytes = null,
        IReadOnlyDictionary<string, byte[]>? artifactBytesByRelativePath = null,
        SessionReplaySource? replaySource = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(imageBytesByFrameId);

        if (string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            return SessionImportResult.Failure("The session archive is missing a session ID.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.AppId))
        {
            return SessionImportResult.Failure("The session archive is missing an app ID.");
        }

        var preferredSessionId = snapshot.SessionId.Trim();
        var resolvedSessionId = ResolveImportedSessionId(preferredSessionId, snapshot.AppId);
        var importedSnapshot = CreateImportedSnapshot(snapshot, resolvedSessionId, replaySource);

        try
        {
            sessionCaptureStore.SaveImportedSnapshot(
                importedSnapshot,
                imageBytesByFrameId,
                appIconBytes,
                artifactBytesByRelativePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return SessionImportResult.Failure($"Could not import the session archive: {ex.Message}");
        }

        lock (gate)
        {
            var importedSession = SessionStateMapper.CreateState(importedSnapshot);
            sessionsById[importedSnapshot.SessionId] = importedSession;
            TouchRetainedLoadedSession(importedSession);
            EnforceRetainedLoadedSessionLimit();
        }

        QueueSessionUpdated(importedSnapshot.SessionId);
        log.Info($"session_imported originalSessionId={preferredSessionId} sessionId={importedSnapshot.SessionId} appId={importedSnapshot.AppId} imageCount={importedSnapshot.Images.Count} analysisCount={importedSnapshot.Analyses.Count} metricSampleCount={importedSnapshot.Metrics.Count}");

        var importMessage = string.Equals(preferredSessionId, importedSnapshot.SessionId, StringComparison.Ordinal)
            ? $"Imported session {importedSnapshot.SessionId}."
            : $"Imported session {preferredSessionId} as {importedSnapshot.SessionId}.";
        return SessionImportResult.Success(importMessage, importedSnapshot);
    }

    public SessionExtractionResult ExtractSessionTimelineRange(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionExtractionResult.Failure("Select a session to extract.");
        }

        if (!SessionTimelineTransformer.TryNormalizeTimelineTrimRange(startUtc, endUtc, out var rangeStartUtc, out var rangeEndUtc))
        {
            return SessionExtractionResult.Failure("Drag a timeline range before extracting a session.");
        }

        return ExtractSessionTimelineRangeCore(
            sessionId.Trim(),
            rangeStartUtc,
            rangeEndUtc,
            name,
            sourceAnnotation: null);
    }

    public SessionExtractionResult ExtractSessionAnnotationBounds(
        string sessionId,
        string annotationId,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionExtractionResult.Failure("Select a session to extract.");
        }

        if (string.IsNullOrWhiteSpace(annotationId))
        {
            return SessionExtractionResult.Failure("Select an annotation to extract.");
        }

        var trimmedSessionId = sessionId.Trim();
        var trimmedAnnotationId = annotationId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return SessionExtractionResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        SessionAnnotation sourceAnnotation;
        DateTimeOffset rangeStartUtc;
        DateTimeOffset rangeEndUtc;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return SessionExtractionResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var annotation = session.Annotations.FirstOrDefault(candidate =>
                string.Equals(candidate.AnnotationId, trimmedAnnotationId, StringComparison.Ordinal));
            if (annotation is null)
            {
                return SessionExtractionResult.Failure($"Annotation '{trimmedAnnotationId}' was not found for session '{trimmedSessionId}'.");
            }

            if (!SessionTimelineTransformer.TryResolveAnnotationExtractionRange(annotation, out rangeStartUtc, out rangeEndUtc))
            {
                return SessionExtractionResult.Failure($"Annotation '{trimmedAnnotationId}' does not define a timeline range to extract.");
            }

            sourceAnnotation = SessionSnapshotCloner.CloneAnnotation(annotation);
        }

        return ExtractSessionTimelineRangeCore(
            trimmedSessionId,
            rangeStartUtc,
            rangeEndUtc,
            name,
            sourceAnnotation);
    }

    public OperationResult UpdateSessionMetadata(
        string sessionId,
        bool isPinned,
        IReadOnlyList<string>? tags,
        string? notes,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to update.");
        }

        var trimmedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        var normalizedTags = NormalizeTags(tags);
        var normalizedNotes = NormalizeNotes(notes);
        var normalizedName = name is null ? null : NormalizeSessionName(name);

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var hasChanged = session.IsPinned != isPinned
                             || !session.Tags.SequenceEqual(normalizedTags, StringComparer.Ordinal)
                             || !string.Equals(session.Notes, normalizedNotes, StringComparison.Ordinal)
                             || (name is not null && !string.Equals(session.Name, normalizedName, StringComparison.Ordinal));
            if (!hasChanged)
            {
                return OperationResult.Success("Session metadata is unchanged.");
            }

            session.IsPinned = isPinned;
            session.Tags.Clear();
            session.Tags.AddRange(normalizedTags);
            session.Notes = normalizedNotes;
            if (name is not null)
            {
                session.Name = normalizedName;
            }

            session.MarkTagsChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_metadata_updated sessionId={trimmedSessionId} isPinned={isPinned} tagCount={normalizedTags.Length} hasName={!string.IsNullOrWhiteSpace(normalizedName)} hasNotes={!string.IsNullOrWhiteSpace(normalizedNotes)}");
        return OperationResult.Success($"Session '{trimmedSessionId}' metadata updated.");
    }

    public OperationResult UpsertSessionAnnotation(string sessionId, SessionAnnotation annotation)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to update.");
        }

        ArgumentNullException.ThrowIfNull(annotation);

        var trimmedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        if (!TryNormalizeAnnotation(annotation, out var normalizedAnnotation, out var validationError) || normalizedAnnotation is null)
        {
            return OperationResult.Failure(validationError ?? "The annotation is invalid.");
        }

        var wasUpdated = false;

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var existingIndex = session.Annotations.FindIndex(candidate =>
                string.Equals(candidate.AnnotationId, normalizedAnnotation.AnnotationId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                wasUpdated = !SessionAnnotationsEqual(session.Annotations[existingIndex], normalizedAnnotation);
                if (!wasUpdated)
                {
                    return OperationResult.Success("Annotation is unchanged.");
                }

                session.Annotations[existingIndex] = normalizedAnnotation;
            }
            else
            {
                session.Annotations.Add(normalizedAnnotation);
            }

            session.Annotations.Sort(CompareAnnotations);
            session.MarkAnnotationsChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_annotation_upserted sessionId={trimmedSessionId} annotationId={normalizedAnnotation.AnnotationId} label={normalizedAnnotation.Label} geometryCount={normalizedAnnotation.Geometry.Count} isUpdate={wasUpdated}");
        return OperationResult.Success($"Annotation '{normalizedAnnotation.AnnotationId}' saved for session '{trimmedSessionId}'.");
    }

    public OperationResult UpsertSessionAgentTaskLink(string sessionId, SessionAgentTaskLink taskLink)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to update.");
        }

        ArgumentNullException.ThrowIfNull(taskLink);

        var trimmedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        if (!TryNormalizeAgentTaskLink(trimmedSessionId, taskLink, out var normalizedTaskLink, out var error)
            || normalizedTaskLink is null)
        {
            return OperationResult.Failure(error ?? "The agent task link is invalid.");
        }

        var wasUpdated = false;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var existingIndex = session.AgentTaskLinks.FindIndex(candidate =>
                string.Equals(candidate.LinkId, normalizedTaskLink.LinkId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                session.AgentTaskLinks[existingIndex] = normalizedTaskLink;
                wasUpdated = true;
            }
            else
            {
                session.AgentTaskLinks.Add(normalizedTaskLink);
            }

            session.AgentTaskLinks.Sort(static (left, right) =>
                right.CreatedAtUtc.CompareTo(left.CreatedAtUtc));
            session.MarkAgentTaskLinksChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_agent_task_link_upserted sessionId={trimmedSessionId} linkId={normalizedTaskLink.LinkId} provider={normalizedTaskLink.Provider} status={normalizedTaskLink.Status} isUpdate={wasUpdated}");
        return OperationResult.Success(
            $"Agent task link '{normalizedTaskLink.LinkId}' saved for session '{trimmedSessionId}'.");
    }

    private static bool TryNormalizeAgentTaskLink(
        string sessionId,
        SessionAgentTaskLink taskLink,
        out SessionAgentTaskLink? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        var linkId = NormalizeTaskLinkValue(taskLink.LinkId, 128);
        var annotationBatchId = NormalizeTaskLinkValue(taskLink.AnnotationBatchId, 128);
        var source = NormalizeTaskLinkValue(taskLink.Source, 128);
        var provider = NormalizeTaskLinkValue(taskLink.Provider, 64);
        var providerTaskId = NormalizeTaskLinkValue(taskLink.ProviderTaskId, 256);
        var status = NormalizeTaskLinkValue(taskLink.Status, 64);
        if (linkId is null
            || annotationBatchId is null
            || source is null
            || provider is null
            || providerTaskId is null
            || status is null
            || string.IsNullOrWhiteSpace(taskLink.SubmittedPrompt)
            || !string.Equals(sessionId, taskLink.SessionId?.Trim(), StringComparison.Ordinal))
        {
            error = "The agent task link is missing required session, provider, task, prompt, or status data.";
            return false;
        }

        var createdAtUtc = taskLink.CreatedAtUtc.ToUniversalTime();
        var updatedAtUtc = taskLink.UpdatedAtUtc.ToUniversalTime();
        if (updatedAtUtc < createdAtUtc)
        {
            updatedAtUtc = createdAtUtc;
        }

        normalized = new SessionAgentTaskLink
        {
            LinkId = linkId,
            WorkContextId = NormalizeTaskLinkValue(taskLink.WorkContextId, 256),
            SessionId = sessionId,
            AnnotationBatchId = annotationBatchId,
            AnnotationIds = NormalizeTaskLinkValues(taskLink.AnnotationIds, 256),
            FrameIds = NormalizeTaskLinkValues(taskLink.FrameIds, 256),
            Source = source,
            Provider = provider.ToLowerInvariant(),
            ProviderTaskId = providerTaskId,
            ProviderTurnId = NormalizeTaskLinkValue(taskLink.ProviderTurnId, 256),
            ProviderTaskTitle = NormalizeTaskLinkValue(taskLink.ProviderTaskTitle, 512),
            WorkingDirectory = string.IsNullOrWhiteSpace(taskLink.WorkingDirectory)
                ? null
                : taskLink.WorkingDirectory.Trim(),
            SubmittedPrompt = taskLink.SubmittedPrompt,
            Status = status,
            StatusMessage = NormalizeTaskLinkValue(taskLink.StatusMessage, 2_048),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc
        };
        return true;
    }

    private static string? NormalizeTaskLinkValue(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static IReadOnlyList<string> NormalizeTaskLinkValues(
        IReadOnlyList<string>? values,
        int maximumLength)
    {
        return values?
                   .Select(value => NormalizeTaskLinkValue(value, maximumLength))
                   .Where(static value => value is not null)
                   .Cast<string>()
                   .Distinct(StringComparer.Ordinal)
                   .ToArray()
               ?? Array.Empty<string>();
    }

    public OperationResult AddSessionVisualTreeSnapshot(string sessionId, SessionVisualTreeSnapshot snapshot)
        => AddSessionVisualTreeSnapshot(sessionId, snapshot, discardIfUnchanged: false).Result;

    public SessionVisualTreeIngestionResult ReceiveSessionVisualTreeSnapshot(
        string sessionId,
        SessionVisualTreeSnapshot snapshot)
        => AddSessionVisualTreeSnapshot(sessionId, snapshot, discardIfUnchanged: true);

    private SessionVisualTreeIngestionResult AddSessionVisualTreeSnapshot(
        string sessionId,
        SessionVisualTreeSnapshot snapshot,
        bool discardIfUnchanged)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionVisualTreeIngestionResult.Failure("Select a session to update.");
        }

        ArgumentNullException.ThrowIfNull(snapshot);

        var trimmedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return SessionVisualTreeIngestionResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        if (!TryNormalizeVisualTreeSnapshot(snapshot, out var normalizedSnapshot) || normalizedSnapshot is null)
        {
            return SessionVisualTreeIngestionResult.Failure("The visual tree snapshot is invalid.");
        }

        var wasUpdated = false;

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return SessionVisualTreeIngestionResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            if (discardIfUnchanged)
            {
                var lastMatchingSnapshot = session.VisualTreeSnapshots.LastOrDefault(candidate =>
                    VisualTreeSnapshotContentFingerprint.RepresentsSameVisualTree(candidate, normalizedSnapshot));
                if (lastMatchingSnapshot is not null
                    && string.Equals(
                        VisualTreeSnapshotContentFingerprint.Compute(lastMatchingSnapshot),
                        VisualTreeSnapshotContentFingerprint.Compute(normalizedSnapshot),
                        StringComparison.Ordinal))
                {
                    log.Info($"session_visual_tree_snapshot_discarded sessionId={trimmedSessionId} snapshotId={normalizedSnapshot.SnapshotId} matchesSnapshotId={lastMatchingSnapshot.SnapshotId}");
                    return SessionVisualTreeIngestionResult.Discarded(
                        $"Visual tree snapshot '{normalizedSnapshot.SnapshotId}' matched the previous snapshot and was discarded.");
                }
            }

            var existingIndex = session.VisualTreeSnapshots.FindIndex(candidate =>
                string.Equals(candidate.SnapshotId, normalizedSnapshot.SnapshotId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                session.VisualTreeSnapshots[existingIndex] = normalizedSnapshot;
                wasUpdated = true;
            }
            else
            {
                session.VisualTreeSnapshots.Add(normalizedSnapshot);
            }

            session.VisualTreeSnapshots.Sort(CompareVisualTreeSnapshots);
            SessionStateMapper.TouchTimelineEnd(session, normalizedSnapshot.CapturedAtUtc);
            session.MarkVisualTreesChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_visual_tree_snapshot_saved sessionId={trimmedSessionId} snapshotId={normalizedSnapshot.SnapshotId} nodeCount={normalizedSnapshot.NodeCount} isUpdate={wasUpdated}");
        return SessionVisualTreeIngestionResult.Saved(
            $"Visual tree snapshot '{normalizedSnapshot.SnapshotId}' saved for session '{trimmedSessionId}'.");
    }

    public OperationResult AddSessionArtifactSnapshot(
        string sessionId,
        SessionArtifactSnapshot snapshot,
        string sourceDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to update.");
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);

        var trimmedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        if (!TryNormalizeArtifactSnapshot(snapshot, out var normalizedSnapshot) || normalizedSnapshot is null)
        {
            return OperationResult.Failure("The artifact snapshot is invalid.");
        }

        string appId;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            appId = session.AppId;
        }

        try
        {
            sessionCaptureStore.SaveSessionArtifactSnapshotFiles(
                appId,
                trimmedSessionId,
                normalizedSnapshot,
                sourceDirectoryPath.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult.Failure($"Could not save artifact snapshot files: {ex.Message}");
        }

        var wasUpdated = false;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var existingIndex = session.ArtifactSnapshots.FindIndex(candidate =>
                string.Equals(candidate.SnapshotId, normalizedSnapshot.SnapshotId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                session.ArtifactSnapshots[existingIndex] = normalizedSnapshot;
                wasUpdated = true;
            }
            else
            {
                session.ArtifactSnapshots.Add(normalizedSnapshot);
            }

            session.ArtifactSnapshots.Sort(CompareArtifactSnapshots);
            SessionStateMapper.TouchTimelineEnd(session, normalizedSnapshot.CapturedAtUtc);
            session.MarkArtifactsChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_artifact_snapshot_saved sessionId={trimmedSessionId} snapshotId={normalizedSnapshot.SnapshotId} fileCount={normalizedSnapshot.FileCount} byteCount={normalizedSnapshot.ByteCount} isUpdate={wasUpdated}");
        return OperationResult.Success($"Artifact snapshot '{normalizedSnapshot.SnapshotId}' saved for session '{trimmedSessionId}'.");
    }

    private SessionExtractionResult ExtractSessionTimelineRangeCore(
        string trimmedSessionId,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        string? name,
        SessionAnnotation? sourceAnnotation)
    {
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return SessionExtractionResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        AppSessionSnapshot sourceSnapshot;
        AppSessionSnapshot extractedSnapshot;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return SessionExtractionResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            if (IsSessionLive(session))
            {
                return SessionExtractionResult.Failure("Live sessions cannot be extracted while they are still active.");
            }

            sourceSnapshot = SessionStateMapper.CreateSnapshot(session);
            var extractedSessionId = ResolveImportedSessionId(
                $"{sourceSnapshot.SessionId}-extract",
                sourceSnapshot.AppId);
            var extractionBaseSnapshot = SessionTimelineTransformer.CreateExtractionBaseSnapshot(
                sourceSnapshot,
                extractedSessionId,
                rangeStartUtc,
                rangeEndUtc,
                name,
                sourceAnnotation);
            var extractedState = SessionStateMapper.CreateState(extractionBaseSnapshot);
            SessionTimelineTransformer.ApplyTimelineTrim(extractedState, rangeStartUtc, rangeEndUtc, SessionTimelineTrimMode.KeepSelectionOnly);
            extractedState.MarkAllContentChanged();
            extractedSnapshot = SessionStateMapper.CreateSnapshot(extractedState);
        }

        try
        {
            sessionCaptureStore.SaveExtractedSnapshot(extractedSnapshot, sourceSnapshot);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return SessionExtractionResult.Failure($"Could not extract session: {ex.Message}");
        }

        lock (gate)
        {
            var extractedSession = SessionStateMapper.CreateState(extractedSnapshot);
            sessionsById[extractedSnapshot.SessionId] = extractedSession;
            TouchRetainedLoadedSession(extractedSession);
            EnforceRetainedLoadedSessionLimit();
        }

        QueueSessionUpdated(extractedSnapshot.SessionId);
        log.Info($"session_extracted sourceSessionId={sourceSnapshot.SessionId} extractedSessionId={extractedSnapshot.SessionId} startUtc={rangeStartUtc:O} endUtc={rangeEndUtc:O} sourceAnnotationId={sourceAnnotation?.AnnotationId ?? string.Empty}");
        return SessionExtractionResult.Success(
            $"Extracted session {extractedSnapshot.SessionId} from {sourceSnapshot.SessionId}.",
            extractedSnapshot);
    }

    public OperationResult DeleteSessionAnnotation(string sessionId, string annotationId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session annotation to delete.");
        }

        if (string.IsNullOrWhiteSpace(annotationId))
        {
            return OperationResult.Failure("Select a session annotation to delete.");
        }

        var trimmedSessionId = sessionId.Trim();
        var trimmedAnnotationId = annotationId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var annotationIndex = session.Annotations.FindIndex(annotation =>
                string.Equals(annotation.AnnotationId, trimmedAnnotationId, StringComparison.Ordinal));
            if (annotationIndex < 0)
            {
                return OperationResult.Failure($"Annotation '{trimmedAnnotationId}' was not found for session '{trimmedSessionId}'.");
            }

            session.Annotations.RemoveAt(annotationIndex);
            session.MarkAnnotationsChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_annotation_deleted sessionId={trimmedSessionId} annotationId={trimmedAnnotationId}");
        return OperationResult.Success($"Annotation '{trimmedAnnotationId}' deleted.");
    }

    public OperationResult DeleteSessionAnalysis(string sessionId, string analysisId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session analysis to delete.");
        }

        if (string.IsNullOrWhiteSpace(analysisId))
        {
            return OperationResult.Failure("Select a session analysis to delete.");
        }

        var trimmedSessionId = sessionId.Trim();
        var trimmedAnalysisId = analysisId.Trim();
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            var analysisIndex = session.Analyses.FindIndex(analysis =>
                string.Equals(analysis.AnalysisId, trimmedAnalysisId, StringComparison.Ordinal));
            if (analysisIndex < 0)
            {
                return OperationResult.Failure($"Analysis '{trimmedAnalysisId}' was not found for session '{trimmedSessionId}'.");
            }

            session.Analyses.RemoveAt(analysisIndex);
            session.MarkAnalysesChanged();
        }

        PersistAndBroadcast(trimmedSessionId);
        log.Info($"session_analysis_deleted sessionId={trimmedSessionId} analysisId={trimmedAnalysisId}");
        return OperationResult.Success($"Analysis '{trimmedAnalysisId}' deleted.");
    }

    public SessionNormalizationResult NormalizeSession(string sessionId)
        => NormalizeSession(sessionId, new SessionOptimizationOptions());

    public SessionNormalizationResult NormalizeSession(
        string sessionId,
        SessionOptimizationOptions options,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionNormalizationResult.Failure("Select a session to normalize.");
        }

        var normalizedSessionId = sessionId.Trim();
        if (!EnsureSessionLoaded(normalizedSessionId))
        {
            return SessionNormalizationResult.Failure($"Session '{normalizedSessionId}' was not found.");
        }

        string appId;
        SessionImageFrame[] images;
        SessionVisualTreeSnapshot[] visualTrees;
        lock (gate)
        {
            if (!sessionsById.TryGetValue(normalizedSessionId, out var session))
            {
                return SessionNormalizationResult.Failure($"Session '{normalizedSessionId}' was not found.");
            }

            if (IsSessionLive(session))
            {
                return SessionNormalizationResult.Failure(
                    "Live sessions cannot be normalized while they are still active.");
            }

            appId = session.AppId;
            images = session.Images.ToArray();
            visualTrees = session.VisualTreeSnapshots.ToArray();
        }

        var plan = sessionNormalizationService.CreatePlan(
            appId,
            normalizedSessionId,
            images,
            visualTrees,
            options,
            report,
            cancellationToken);

        AppSessionSnapshot snapshot;
        SessionNormalizationChanges changes;
        cancellationToken.ThrowIfCancellationRequested();
        report?.Invoke(new("Updating session evidence references…"));
        lock (gate)
        {
            if (!sessionsById.TryGetValue(normalizedSessionId, out var session))
            {
                return SessionNormalizationResult.Failure($"Session '{normalizedSessionId}' was not found.");
            }

            if (IsSessionLive(session))
            {
                return SessionNormalizationResult.Failure(
                    "Live sessions cannot be normalized while they are still active.");
            }

            changes = sessionNormalizationService.Apply(session, plan);
            if (!changes.HasChanges)
            {
                return SessionNormalizationResult.Success(
                    "Session evidence is already normalized.",
                    0,
                    0);
            }

            snapshot = SessionStateMapper.CreateSnapshot(session);
        }

        report?.Invoke(new("Saving deduplicated session evidence…"));
        sessionCaptureStore.SaveAndPruneDetachedContent(snapshot);
        QueueSessionUpdated(normalizedSessionId);
        log.Info(
            $"session_normalized sessionId={normalizedSessionId} "
            + $"removedScreenshotCount={changes.RemovedScreenshotCount} "
            + $"removedVisualTreeSnapshotCount={changes.RemovedVisualTreeSnapshotCount}");

        return SessionNormalizationResult.Success(
            $"Normalized session '{normalizedSessionId}': removed {changes.RemovedScreenshotCount:N0} duplicate "
            + $"{(changes.RemovedScreenshotCount == 1 ? "screenshot" : "screenshots")} and "
            + $"{changes.RemovedVisualTreeSnapshotCount:N0} duplicate "
            + $"{(changes.RemovedVisualTreeSnapshotCount == 1 ? "visual tree" : "visual trees")}.",
            changes.RemovedScreenshotCount,
            changes.RemovedVisualTreeSnapshotCount);
    }

    public OperationResult TrimSessionTimeline(
        string sessionId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        SessionTimelineTrimMode mode,
        Action<SessionTimelineTrimProgress>? report = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to trim.");
        }

        if (!SessionTimelineTransformer.TryNormalizeTimelineTrimRange(startUtc, endUtc, out var rangeStartUtc, out var rangeEndUtc))
        {
            return OperationResult.Failure("Drag a timeline range before using the trim tool.");
        }

        if (!Enum.IsDefined(mode))
        {
            return OperationResult.Failure("Select a trim action.");
        }

        var trimmedSessionId = sessionId.Trim();
        report?.Invoke(new("Loading session evidence…"));
        if (!EnsureSessionLoaded(trimmedSessionId))
        {
            return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
        }

        AppSessionSnapshot? snapshot = null;

        lock (gate)
        {
            if (!sessionsById.TryGetValue(trimmedSessionId, out var session))
            {
                return OperationResult.Failure($"Session '{trimmedSessionId}' was not found.");
            }

            if (IsSessionLive(session))
            {
                return OperationResult.Failure("Live sessions cannot be trimmed while they are still active.");
            }

            if (!SessionTimelineTransformer.ApplyTimelineTrim(session, rangeStartUtc, rangeEndUtc, mode, report))
            {
                return OperationResult.Success("Session timeline is unchanged.");
            }

            session.MarkAllContentChanged();
            report?.Invoke(new("Updating session evidence references…"));
            snapshot = SessionStateMapper.CreateSnapshot(session);
        }

        sessionCaptureStore.SaveAndPruneDetachedContent(snapshot, message => report?.Invoke(new(message)));
        QueueSessionUpdated(trimmedSessionId);
        log.Info($"session_timeline_trimmed sessionId={trimmedSessionId} mode={mode} startUtc={rangeStartUtc:O} endUtc={rangeEndUtc:O}");
        return OperationResult.Success(mode == SessionTimelineTrimMode.CutSelection
            ? "Cut the selected timeline range from the session."
            : "Trimmed the session to the selected timeline range.");
    }

    public OperationResult DeleteSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Select a session to delete.");
        }

        SessionState? removedSession = null;

        lock (gate)
        {
            if (sessionsById.TryGetValue(sessionId, out var loadedSession))
            {
                if (IsSessionLive(loadedSession))
                {
                    return OperationResult.Failure("Live sessions cannot be deleted while they are still active.");
                }

                removedSession = loadedSession;
                sessionsById.Remove(sessionId);
                RemoveRetainedLoadedSession(sessionId);
            }
        }

        var deletedPersistedCapture = sessionCaptureStore.Delete(sessionId);
        if (removedSession is null && !deletedPersistedCapture)
        {
            return OperationResult.Failure($"Session '{sessionId}' was not found.");
        }

        if (removedSession is not null && !deletedPersistedCapture)
        {
            lock (gate)
            {
                sessionsById[sessionId] = removedSession;
                TouchRetainedLoadedSession(removedSession);
                EnforceRetainedLoadedSessionLimit();
            }

            return OperationResult.Failure($"Session '{sessionId}' could not be deleted from the capture store.");
        }

        lock (broadcastGate)
        {
            pendingSessionUpdateIds.Remove(sessionId);
            pendingSessionPersistenceIds.Remove(sessionId);
            lastSessionPersistenceUtcById.Remove(sessionId);
        }

        SessionDeleted?.Invoke(this, sessionId);
        log.Info($"session_deleted sessionId={sessionId}");
        return OperationResult.Success($"Session '{sessionId}' deleted.");
    }

}
