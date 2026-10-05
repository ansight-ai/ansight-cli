namespace Ansight.Host.Models.Session;

using System.Text.Json.Nodes;

public static class SessionSnapshotCloner
{
    public static AppSessionSnapshot WithLogs(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionLogStream> logStreams,
        IReadOnlyList<LogEntry> logs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(logStreams);
        ArgumentNullException.ThrowIfNull(logs);
        return new AppSessionSnapshot
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
            IsHistorical = snapshot.IsHistorical,
            CacheSizeBytes = snapshot.CacheSizeBytes,
            IsPinned = snapshot.IsPinned,
            Author = snapshot.Author,
            ReplaySource = snapshot.ReplaySource,
            CaptureSource = snapshot.CaptureSource,
            SdkVersion = snapshot.SdkVersion,
            Tags = snapshot.Tags,
            Notes = snapshot.Notes,
            CustomProperties = CloneCustomProperties(snapshot.CustomProperties),
            AppState = snapshot.AppState,
            AppStateChangedUtc = snapshot.AppStateChangedUtc,
            DeviceProfile = snapshot.DeviceProfile,
            DeviceProfileJson = snapshot.DeviceProfileJson,
            AppIcon = snapshot.AppIcon,
            AppToolCatalog = CloneAppToolCatalog(snapshot.AppToolCatalog),
            Analyses = snapshot.Analyses,
            Annotations = snapshot.Annotations,
            AgentTaskLinks = snapshot.AgentTaskLinks,
            Images = snapshot.Images,
            Touches = snapshot.Touches,
            NetworkRequests = snapshot.NetworkRequests,
            VisualTreeSnapshots = snapshot.VisualTreeSnapshots,
            ArtifactSnapshots = snapshot.ArtifactSnapshots,
            ApplicationEvents = snapshot.ApplicationEvents,
            LogStreams = logStreams,
            Logs = logs,
            TotalLogCount = Math.Max(snapshot.TotalLogCount, logs.Count),
            RetainedLogStartIndex = 0,
            MetricChannels = snapshot.MetricChannels,
            Metrics = snapshot.Metrics,
            TotalApplicationEventCount = snapshot.TotalApplicationEventCount,
            TotalNetworkRequestCount = snapshot.TotalNetworkRequestCount
        };
    }

    public static JsonObject? CloneCustomProperties(JsonObject? customProperties)
    {
        return customProperties?.DeepClone() as JsonObject;
    }

    public static SessionAppToolCatalogSnapshot? CloneAppToolCatalog(
        SessionAppToolCatalogSnapshot? catalog)
        => catalog is null
            ? null
            : catalog with
            {
                ToolCatalog = catalog.ToolCatalog.DeepClone().AsObject(),
                ArtifactCatalog = catalog.ArtifactCatalog?.DeepClone().AsObject()
            };

    public static SessionAnnotation CloneAnnotation(SessionAnnotation annotation)
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
            Evidence = annotation.Evidence
                .Select(CloneAnnotationEvidence)
                .ToArray(),
            HookFailures = annotation.HookFailures.ToArray(),
            Geometry = annotation.Geometry
                .Select(CloneAnnotationGeometry)
                .ToArray(),
            Target = CloneAnnotationTarget(annotation.Target)
        };
    }

    public static SessionAgentTaskLink CloneAgentTaskLink(
        SessionAgentTaskLink taskLink,
        string? sessionId = null)
    {
        return new SessionAgentTaskLink
        {
            LinkId = taskLink.LinkId,
            WorkContextId = taskLink.WorkContextId,
            SessionId = sessionId ?? taskLink.SessionId,
            AnnotationBatchId = taskLink.AnnotationBatchId,
            AnnotationIds = taskLink.AnnotationIds.ToArray(),
            FrameIds = taskLink.FrameIds.ToArray(),
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

    public static SessionAnnotationGeometry CloneAnnotationGeometry(SessionAnnotationGeometry geometry)
    {
        return new SessionAnnotationGeometry
        {
            GeometryId = geometry.GeometryId,
            FrameId = geometry.FrameId,
            CapturedAtUtc = geometry.CapturedAtUtc,
            Kind = geometry.Kind,
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            Points = geometry.Points.Select(CloneAnnotationGeometryPoint).ToArray(),
            Text = geometry.Text,
            StrokeColor = geometry.StrokeColor,
            StrokeWidth = geometry.StrokeWidth
        };
    }

    public static SessionAnnotationGeometryPoint CloneAnnotationGeometryPoint(SessionAnnotationGeometryPoint point)
    {
        return new SessionAnnotationGeometryPoint
        {
            X = point.X,
            Y = point.Y
        };
    }

    public static SessionAnnotationEvidence CloneAnnotationEvidence(SessionAnnotationEvidence evidence)
    {
        return new SessionAnnotationEvidence
        {
            Id = evidence.Id,
            Kind = evidence.Kind,
            Status = evidence.Status,
            Reason = evidence.Reason,
            CapturedAtUtc = evidence.CapturedAtUtc,
            SizeBytes = evidence.SizeBytes,
            Truncated = evidence.Truncated
        };
    }

    public static SessionAnnotationTarget? CloneAnnotationTarget(SessionAnnotationTarget? target)
    {
        return target is null
            ? null
            : new SessionAnnotationTarget
            {
                Kind = target.Kind,
                Source = target.Source,
                TargetId = target.TargetId,
                VisualTreeSnapshotId = target.VisualTreeSnapshotId,
                Type = target.Type,
                ElementKind = target.ElementKind,
                Label = target.Label,
                AutomationId = target.AutomationId,
                Depth = target.Depth,
                ChildCount = target.ChildCount,
                AbsoluteBounds = CloneAnnotationTargetBounds(target.AbsoluteBounds),
                NormalizedBounds = CloneAnnotationTargetBounds(target.NormalizedBounds)
            };
    }

    public static SessionAnnotationTargetBounds? CloneAnnotationTargetBounds(SessionAnnotationTargetBounds? bounds)
    {
        return bounds is null
            ? null
            : new SessionAnnotationTargetBounds
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height
            };
    }

    public static SessionVisualTreeSnapshot CloneVisualTreeSnapshot(SessionVisualTreeSnapshot snapshot)
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
            ScreenshotFrameId = snapshot.ScreenshotFrameId,
            ScreenshotCapturedAtUtc = snapshot.ScreenshotCapturedAtUtc,
            ActionId = snapshot.ActionId,
            ActionCapability = snapshot.ActionCapability,
            EvidencePhase = snapshot.EvidencePhase,
            TreeHash = snapshot.TreeHash,
            ScreenshotHash = snapshot.ScreenshotHash,
            Payload = snapshot.Payload.DeepClone() as JsonObject ?? new JsonObject()
        };
    }

    public static SessionArtifactSnapshot CloneArtifactSnapshot(SessionArtifactSnapshot snapshot)
    {
        return new SessionArtifactSnapshot
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
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
                .Select(CloneArtifactEntry)
                .ToArray()
        };
    }

    public static SessionArtifactEntry CloneArtifactEntry(SessionArtifactEntry entry)
    {
        return new SessionArtifactEntry
        {
            Name = entry.Name,
            RootAlias = entry.RootAlias,
            RelativePath = entry.RelativePath,
            SnapshotRelativePath = entry.SnapshotRelativePath,
            Kind = entry.Kind,
            SizeBytes = entry.SizeBytes,
            FileExtension = entry.FileExtension,
            MimeType = entry.MimeType,
            LastModifiedUtc = entry.LastModifiedUtc,
            ArchiveRelativePath = entry.ArchiveRelativePath
        };
    }
}
