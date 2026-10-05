namespace Ansight.Host.Sessions;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal sealed class SessionNormalizationService
{
    private readonly string capturesRootPath;

    public SessionNormalizationService(string capturesRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturesRootPath);
        this.capturesRootPath = capturesRootPath;
    }

    public SessionNormalizationPlan CreatePlan(
        string appId,
        string sessionId,
        IReadOnlyList<SessionImageFrame> images,
        IReadOnlyList<SessionVisualTreeSnapshot> visualTrees,
        SessionOptimizationOptions? options = null,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(visualTrees);

        var resolvedOptions = options ?? new SessionOptimizationOptions();
        var selectedVisualTrees = visualTrees.Where(resolvedOptions.ShouldOptimizeVisualTree).ToArray();
        return new SessionNormalizationPlan(
            resolvedOptions.OptimizeScreenshots
                ? FindSequentialDuplicateImages(appId.Trim(), sessionId.Trim(), images, report, cancellationToken)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            selectedVisualTrees.Length > 0
                ? FindSequentialDuplicateVisualTrees(selectedVisualTrees, report, cancellationToken)
                : new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public SessionNormalizationChanges Apply(
        SessionState session,
        SessionNormalizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);

        var currentImageIds = session.Images
            .Select(static frame => frame.FrameId)
            .ToHashSet(StringComparer.Ordinal);
        var imageReplacements = plan.ImageReplacements
            .Where(replacement => currentImageIds.Contains(replacement.Key)
                                  && currentImageIds.Contains(replacement.Value))
            .ToDictionary(
                static replacement => replacement.Key,
                static replacement => replacement.Value,
                StringComparer.Ordinal);
        var currentVisualTreeIds = session.VisualTreeSnapshots
            .Select(static snapshot => snapshot.SnapshotId)
            .ToHashSet(StringComparer.Ordinal);
        var visualTreeReplacements = plan.VisualTreeReplacements
            .Where(replacement => currentVisualTreeIds.Contains(replacement.Key)
                                  && currentVisualTreeIds.Contains(replacement.Value))
            .ToDictionary(
                static replacement => replacement.Key,
                static replacement => replacement.Value,
                StringComparer.Ordinal);

        var removedScreenshotCount = session.Images.RemoveAll(frame =>
            imageReplacements.ContainsKey(frame.FrameId));
        var removedVisualTreeSnapshotCount = session.VisualTreeSnapshots.RemoveAll(snapshot =>
            visualTreeReplacements.ContainsKey(snapshot.SnapshotId));
        if (removedScreenshotCount == 0 && removedVisualTreeSnapshotCount == 0)
        {
            return SessionNormalizationChanges.None;
        }

        RemapEvidenceReferences(session, imageReplacements, visualTreeReplacements);
        session.MarkAllContentChanged();
        return new SessionNormalizationChanges(
            removedScreenshotCount,
            removedVisualTreeSnapshotCount);
    }

    private Dictionary<string, string> FindSequentialDuplicateImages(
        string appId,
        string sessionId,
        IReadOnlyList<SessionImageFrame> frames,
        Action<SessionOptimizationProgress>? report,
        CancellationToken cancellationToken)
    {
        var duplicates = new Dictionary<string, string>(StringComparer.Ordinal);
        SessionImageFrame? previousRetainedFrame = null;
        string? previousFingerprint = null;

        var orderedFrames = frames
            .Where(static frame => !string.IsNullOrWhiteSpace(frame.FrameId))
            .OrderBy(static frame => frame.CapturedAtUtc)
            .ThenBy(static frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        const string message = "Checking screenshots for duplicates…";
        report?.Invoke(new(message, 0, orderedFrames.Length));
        for (var index = 0; index < orderedFrames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = orderedFrames[index];
            var filePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                capturesRootPath,
                appId,
                sessionId,
                frame);
            var fingerprint = TryComputeFileFingerprint(filePath);
            if (fingerprint is not null
                && previousFingerprint is not null
                && previousRetainedFrame is not null
                && string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal))
            {
                duplicates[frame.FrameId] = previousRetainedFrame.FrameId;
                ReportScanProgress(report, message, index + 1, orderedFrames.Length);
                continue;
            }

            previousRetainedFrame = frame;
            previousFingerprint = fingerprint;
            ReportScanProgress(report, message, index + 1, orderedFrames.Length);
        }

        return duplicates;
    }

    private static Dictionary<string, string> FindSequentialDuplicateVisualTrees(
        IReadOnlyList<SessionVisualTreeSnapshot> snapshots,
        Action<SessionOptimizationProgress>? report,
        CancellationToken cancellationToken)
    {
        var duplicates = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastRetainedByType = new Dictionary<VisualTreeType, RetainedVisualTree>();
        var orderedSnapshots = snapshots
            .OrderBy(static snapshot => snapshot.CapturedAtUtc)
            .ThenBy(static snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        const string message = "Checking visual trees for duplicates…";
        report?.Invoke(new(message, 0, orderedSnapshots.Length));
        for (var index = 0; index < orderedSnapshots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = orderedSnapshots[index];
            var type = VisualTreeType.FromSnapshot(snapshot);
            var fingerprint = VisualTreeSnapshotContentFingerprint.Compute(snapshot);
            if (lastRetainedByType.TryGetValue(type, out var previous)
                && string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                duplicates[snapshot.SnapshotId] = previous.SnapshotId;
                ReportScanProgress(report, message, index + 1, orderedSnapshots.Length);
                continue;
            }

            lastRetainedByType[type] = new RetainedVisualTree(snapshot.SnapshotId, fingerprint);
            ReportScanProgress(report, message, index + 1, orderedSnapshots.Length);
        }

        return duplicates;
    }

    private static void ReportScanProgress(
        Action<SessionOptimizationProgress>? report,
        string message,
        int completed,
        int total)
    {
        if (report is null || (completed < total && completed % Math.Max(1, total / 100) != 0))
            return;

        report(new(message, completed, total));
    }

    private static string? TryComputeFileFingerprint(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RemapEvidenceReferences(
        SessionState session,
        IReadOnlyDictionary<string, string> imageReplacements,
        IReadOnlyDictionary<string, string> visualTreeReplacements)
    {
        for (var index = 0; index < session.VisualTreeSnapshots.Count; index++)
        {
            var snapshot = session.VisualTreeSnapshots[index];
            var screenshotFrameId = RemapOptionalId(snapshot.ScreenshotFrameId, imageReplacements);
            if (!string.Equals(snapshot.ScreenshotFrameId, screenshotFrameId, StringComparison.Ordinal))
            {
                session.VisualTreeSnapshots[index] = CloneVisualTreeWithScreenshotFrameId(
                    snapshot,
                    screenshotFrameId);
            }
        }

        for (var index = 0; index < session.Annotations.Count; index++)
        {
            var annotation = session.Annotations[index];
            var geometry = annotation.Geometry
                .Select(item => CloneGeometryWithFrameId(
                    item,
                    RemapRequiredId(item.FrameId, imageReplacements)))
                .ToArray();
            var target = CloneTargetWithVisualTreeSnapshotId(
                annotation.Target,
                RemapOptionalId(annotation.Target?.VisualTreeSnapshotId, visualTreeReplacements));
            session.Annotations[index] = CloneAnnotationWithReferences(annotation, geometry, target);
        }

        for (var index = 0; index < session.AgentTaskLinks.Count; index++)
        {
            var taskLink = session.AgentTaskLinks[index];
            var frameIds = taskLink.FrameIds
                .Select(frameId => RemapRequiredId(frameId, imageReplacements))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            session.AgentTaskLinks[index] = CloneTaskLinkWithFrameIds(taskLink, frameIds);
        }
    }

    private static SessionVisualTreeSnapshot CloneVisualTreeWithScreenshotFrameId(
        SessionVisualTreeSnapshot snapshot,
        string? screenshotFrameId)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
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
            ScreenshotFrameId = screenshotFrameId,
            ScreenshotCapturedAtUtc = snapshot.ScreenshotCapturedAtUtc,
            ActionId = snapshot.ActionId,
            ActionCapability = snapshot.ActionCapability,
            EvidencePhase = snapshot.EvidencePhase,
            TreeHash = snapshot.TreeHash,
            ScreenshotHash = snapshot.ScreenshotHash,
            Payload = snapshot.Payload.DeepClone() as JsonObject ?? new JsonObject()
        };
    }

    private static SessionAnnotation CloneAnnotationWithReferences(
        SessionAnnotation annotation,
        IReadOnlyList<SessionAnnotationGeometry> geometry,
        SessionAnnotationTarget? target)
    {
        return new SessionAnnotation
        {
            AnnotationId = annotation.AnnotationId,
            StartUtc = annotation.StartUtc,
            EndUtc = annotation.EndUtc,
            Label = annotation.Label,
            Source = annotation.Source,
            Notes = annotation.Notes,
            Status = annotation.Status,
            CaptureGroupId = annotation.CaptureGroupId,
            CustomData = annotation.CustomData?.DeepClone() as JsonObject,
            Evidence = annotation.Evidence.Select(SessionSnapshotCloner.CloneAnnotationEvidence).ToArray(),
            HookFailures = annotation.HookFailures.ToArray(),
            Geometry = geometry,
            Target = target
        };
    }

    private static SessionAnnotationGeometry CloneGeometryWithFrameId(
        SessionAnnotationGeometry geometry,
        string frameId)
    {
        return new SessionAnnotationGeometry
        {
            GeometryId = geometry.GeometryId,
            FrameId = frameId,
            CapturedAtUtc = geometry.CapturedAtUtc,
            Kind = geometry.Kind,
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            Points = geometry.Points.Select(SessionSnapshotCloner.CloneAnnotationGeometryPoint).ToArray(),
            Text = geometry.Text,
            StrokeColor = geometry.StrokeColor,
            StrokeWidth = geometry.StrokeWidth
        };
    }

    private static SessionAnnotationTarget? CloneTargetWithVisualTreeSnapshotId(
        SessionAnnotationTarget? target,
        string? visualTreeSnapshotId)
    {
        if (target is null)
        {
            return null;
        }

        return new SessionAnnotationTarget
        {
            Kind = target.Kind,
            Source = target.Source,
            TargetId = target.TargetId,
            VisualTreeSnapshotId = visualTreeSnapshotId ?? string.Empty,
            Type = target.Type,
            ElementKind = target.ElementKind,
            Label = target.Label,
            AutomationId = target.AutomationId,
            Depth = target.Depth,
            ChildCount = target.ChildCount,
            AbsoluteBounds = SessionSnapshotCloner.CloneAnnotationTargetBounds(target.AbsoluteBounds),
            NormalizedBounds = SessionSnapshotCloner.CloneAnnotationTargetBounds(target.NormalizedBounds)
        };
    }

    private static SessionAgentTaskLink CloneTaskLinkWithFrameIds(
        SessionAgentTaskLink taskLink,
        IReadOnlyList<string> frameIds)
    {
        return new SessionAgentTaskLink
        {
            LinkId = taskLink.LinkId,
            WorkContextId = taskLink.WorkContextId,
            SessionId = taskLink.SessionId,
            AnnotationBatchId = taskLink.AnnotationBatchId,
            AnnotationIds = taskLink.AnnotationIds.ToArray(),
            FrameIds = frameIds,
            Source = taskLink.Source,
            Provider = taskLink.Provider,
            ProviderTaskId = taskLink.ProviderTaskId,
            ProviderTurnId = taskLink.ProviderTurnId,
            ProviderTaskTitle = taskLink.ProviderTaskTitle,
            WorkingDirectory = taskLink.WorkingDirectory,
            SubmittedPrompt = taskLink.SubmittedPrompt,
            Status = taskLink.Status,
            StatusMessage = taskLink.StatusMessage,
            CreatedAtUtc = taskLink.CreatedAtUtc,
            UpdatedAtUtc = taskLink.UpdatedAtUtc
        };
    }

    private static string RemapRequiredId(
        string id,
        IReadOnlyDictionary<string, string> replacements)
        => replacements.TryGetValue(id, out var replacement) ? replacement : id;

    private static string? RemapOptionalId(
        string? id,
        IReadOnlyDictionary<string, string> replacements)
        => id is not null && replacements.TryGetValue(id, out var replacement) ? replacement : id;

    private readonly record struct VisualTreeType(
        string VisualTreeKind,
        string VisualTreeFormat,
        string RuntimePlatform,
        string RootScope)
    {
        public static VisualTreeType FromSnapshot(SessionVisualTreeSnapshot snapshot)
            => new(
                snapshot.VisualTreeKind,
                snapshot.VisualTreeFormat,
                snapshot.RuntimePlatform,
                snapshot.RootScope);
    }

    private readonly record struct RetainedVisualTree(string SnapshotId, string Fingerprint);
}

internal sealed record SessionNormalizationPlan(
    IReadOnlyDictionary<string, string> ImageReplacements,
    IReadOnlyDictionary<string, string> VisualTreeReplacements);

internal readonly record struct SessionNormalizationChanges(
    int RemovedScreenshotCount,
    int RemovedVisualTreeSnapshotCount)
{
    public static SessionNormalizationChanges None => new(0, 0);

    public bool HasChanges => RemovedScreenshotCount > 0 || RemovedVisualTreeSnapshotCount > 0;
}
