namespace Ansight.Host.Runtime.State;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;

internal static class RuntimeSnapshotNormalizer
{
    internal static bool TryNormalizeAnnotation(
        SessionAnnotation annotation,
        out SessionAnnotation? normalizedAnnotation,
        out string? error)
    {
        error = null;
        normalizedAnnotation = null;

        if (string.IsNullOrWhiteSpace(annotation.AnnotationId))
        {
            error = "Annotation id is required.";
            return false;
        }

        var label = annotation.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            error = "Annotation label is required.";
            return false;
        }

        var startUtc = annotation.StartUtc.ToUniversalTime();
        var endUtc = annotation.EndUtc?.ToUniversalTime();
        if (endUtc.HasValue && endUtc.Value < startUtc)
        {
            (startUtc, endUtc) = (endUtc.Value, startUtc);
        }

        if (endUtc.HasValue && endUtc.Value == startUtc)
        {
            endUtc = null;
        }

        var normalizedGeometry = NormalizeGeometry(annotation.Geometry)
            .OrderBy(geometry => geometry.CapturedAtUtc)
            .ThenBy(geometry => geometry.GeometryId, StringComparer.Ordinal)
            .ToArray();

        normalizedAnnotation = new SessionAnnotation
        {
            AnnotationId = annotation.AnnotationId.Trim(),
            StartUtc = startUtc,
            EndUtc = endUtc,
            Label = label,
            Source = NormalizeAnnotationSource(annotation.Source),
            Notes = RuntimeState.NormalizeNotes(annotation.Notes),
            Status = RuntimeState.NormalizeNotes(annotation.Status),
            CaptureGroupId = RuntimeState.NormalizeNotes(annotation.CaptureGroupId),
            CustomData = annotation.CustomData?.DeepClone() as JsonObject,
            Evidence = NormalizeAnnotationEvidence(annotation.Evidence),
            HookFailures = annotation.HookFailures
                .Where(failure => !string.IsNullOrWhiteSpace(failure))
                .Select(failure => failure.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Geometry = normalizedGeometry,
            Target = NormalizeAnnotationTarget(annotation.Target)
        };
        return true;
    }

    internal static string NormalizeAnnotationSource(string? source)
    {
        return string.IsNullOrWhiteSpace(source)
            ? RuntimeState.DefaultSessionAnnotationSource
            : source.Trim();
    }

    internal static SessionAnnotationTarget? NormalizeAnnotationTarget(SessionAnnotationTarget? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.TargetId))
        {
            return null;
        }

        return new SessionAnnotationTarget
        {
            Kind = string.IsNullOrWhiteSpace(target.Kind) ? "visualTreeElement" : target.Kind.Trim(),
            Source = target.Source?.Trim() ?? string.Empty,
            TargetId = target.TargetId.Trim(),
            VisualTreeSnapshotId = target.VisualTreeSnapshotId?.Trim() ?? string.Empty,
            Type = target.Type?.Trim() ?? string.Empty,
            ElementKind = target.ElementKind?.Trim() ?? string.Empty,
            Label = target.Label?.Trim() ?? string.Empty,
            AutomationId = target.AutomationId?.Trim() ?? string.Empty,
            Depth = Math.Max(0, target.Depth),
            ChildCount = Math.Max(0, target.ChildCount),
            AbsoluteBounds = NormalizeAnnotationTargetBounds(target.AbsoluteBounds, clampToUnitInterval: false),
            NormalizedBounds = NormalizeAnnotationTargetBounds(target.NormalizedBounds, clampToUnitInterval: true)
        };
    }

    internal static SessionAnnotationTargetBounds? NormalizeAnnotationTargetBounds(
        SessionAnnotationTargetBounds? bounds,
        bool clampToUnitInterval)
    {
        if (bounds is null
            || !double.IsFinite(bounds.X)
            || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width)
            || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0d
            || bounds.Height <= 0d)
        {
            return null;
        }

        if (!clampToUnitInterval)
        {
            return new SessionAnnotationTargetBounds
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height
            };
        }

        var x = Math.Clamp(bounds.X, 0d, 1d);
        var y = Math.Clamp(bounds.Y, 0d, 1d);
        return new SessionAnnotationTargetBounds
        {
            X = x,
            Y = y,
            Width = Math.Clamp(bounds.Width, 0d, 1d - x),
            Height = Math.Clamp(bounds.Height, 0d, 1d - y)
        };
    }

    internal static SessionAnnotation[] NormalizeAnnotations(IEnumerable<SessionAnnotation>? annotations)
    {
        if (annotations is null)
        {
            return Array.Empty<SessionAnnotation>();
        }

        var normalized = new List<SessionAnnotation>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (!TryNormalizeAnnotation(annotation, out var normalizedAnnotation, out _)
                || normalizedAnnotation is null
                || !seenIds.Add(normalizedAnnotation.AnnotationId))
            {
                continue;
            }

            normalized.Add(normalizedAnnotation);
        }

        normalized.Sort(CompareAnnotations);
        return normalized.ToArray();
    }

    internal static SessionTouchInputRecord[] NormalizeTouches(IEnumerable<SessionTouchInputRecord>? touches)
    {
        if (touches is null)
        {
            return Array.Empty<SessionTouchInputRecord>();
        }

        var normalized = new List<SessionTouchInputRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var touch in touches)
        {
            if (!TryNormalizeTouch(touch, out var normalizedTouch)
                || normalizedTouch is null
                || !seenIds.Add(normalizedTouch.Id))
            {
                continue;
            }

            normalized.Add(normalizedTouch);
        }

        return normalized
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool TryNormalizeTouch(SessionTouchInputRecord? touch, out SessionTouchInputRecord? normalizedTouch)
    {
        normalizedTouch = null;
        if (touch is null
            || string.IsNullOrWhiteSpace(touch.Id)
            || string.IsNullOrWhiteSpace(touch.Action)
            || string.IsNullOrWhiteSpace(touch.CoordinateUnit)
            || double.IsNaN(touch.X)
            || double.IsInfinity(touch.X)
            || double.IsNaN(touch.Y)
            || double.IsInfinity(touch.Y))
        {
            return false;
        }

        var pointerIndex = Math.Max(0, touch.PointerIndex);
        var pointerCount = Math.Max(1, touch.PointerCount);
        normalizedTouch = new SessionTouchInputRecord
        {
            Id = touch.Id.Trim(),
            Action = touch.Action.Trim().ToLowerInvariant(),
            CapturedAtUtc = touch.CapturedAtUtc.ToUniversalTime(),
            PointerId = touch.PointerId,
            PointerIndex = pointerIndex,
            PointerCount = Math.Max(pointerIndex + 1, pointerCount),
            X = touch.X,
            Y = touch.Y,
            NormalizedX = NormalizeUnitValue(touch.NormalizedX),
            NormalizedY = NormalizeUnitValue(touch.NormalizedY),
            SurfaceWidth = NormalizePositiveValue(touch.SurfaceWidth),
            SurfaceHeight = NormalizePositiveValue(touch.SurfaceHeight),
            CoordinateSpace = string.IsNullOrWhiteSpace(touch.CoordinateSpace)
                ? "window"
                : touch.CoordinateSpace.Trim(),
            CoordinateUnit = touch.CoordinateUnit.Trim(),
            SurfaceScale = NormalizePositiveValue(touch.SurfaceScale),
            Details = touch.Details
        };
        return true;
    }

    internal static double? NormalizeUnitValue(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0d, 1d);
    }

    internal static double? NormalizePositiveValue(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value <= 0)
        {
            return null;
        }

        return value.Value;
    }

    internal static SessionVisualTreeSnapshot[] NormalizeVisualTreeSnapshots(IEnumerable<SessionVisualTreeSnapshot>? snapshots)
    {
        if (snapshots is null)
        {
            return Array.Empty<SessionVisualTreeSnapshot>();
        }

        var normalized = new List<SessionVisualTreeSnapshot>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (TryNormalizeVisualTreeSnapshot(snapshot, out var normalizedSnapshot)
                && normalizedSnapshot is not null
                && seenIds.Add(normalizedSnapshot.SnapshotId))
            {
                normalized.Add(normalizedSnapshot);
            }
        }

        return normalized
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static SessionArtifactSnapshot[] NormalizeArtifactSnapshots(IEnumerable<SessionArtifactSnapshot>? snapshots)
    {
        if (snapshots is null)
        {
            return Array.Empty<SessionArtifactSnapshot>();
        }

        var normalized = new List<SessionArtifactSnapshot>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (TryNormalizeArtifactSnapshot(snapshot, out var normalizedSnapshot)
                && normalizedSnapshot is not null
                && seenIds.Add(normalizedSnapshot.SnapshotId))
            {
                normalized.Add(normalizedSnapshot);
            }
        }

        return normalized
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ThenBy(snapshot => snapshot.SnapshotId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool TryNormalizeArtifactSnapshot(
        SessionArtifactSnapshot snapshot,
        out SessionArtifactSnapshot? normalizedSnapshot)
    {
        normalizedSnapshot = null;
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId)
            || string.IsNullOrWhiteSpace(snapshot.RootAlias)
            || string.IsNullOrWhiteSpace(snapshot.ArtifactDirectoryName))
        {
            return false;
        }

        var entries = NormalizeArtifactEntries(snapshot.Entries);
        normalizedSnapshot = new SessionArtifactSnapshot
        {
            SnapshotId = snapshot.SnapshotId.Trim(),
            CapturedAtUtc = snapshot.CapturedAtUtc.ToUniversalTime(),
            Source = string.IsNullOrWhiteSpace(snapshot.Source) ? "ansight.artifacts" : snapshot.Source.Trim(),
            RootAlias = snapshot.RootAlias.Trim(),
            RootPath = snapshot.RootPath?.Trim() ?? string.Empty,
            RelativePath = SessionArtifactPathNormalizer.Normalize(snapshot.RelativePath),
            Name = string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.RootAlias.Trim() : snapshot.Name.Trim(),
            Kind = string.IsNullOrWhiteSpace(snapshot.Kind) ? "directory" : snapshot.Kind.Trim(),
            ArtifactDirectoryName = FileNameUtil.Sanitize(snapshot.ArtifactDirectoryName),
            DirectoryCount = entries.Count(entry => IsArtifactDirectory(entry.Kind)),
            FileCount = entries.Count(entry => !IsArtifactDirectory(entry.Kind)),
            ByteCount = entries.Where(entry => !IsArtifactDirectory(entry.Kind)).Sum(entry => Math.Max(0, entry.SizeBytes)),
            Truncated = snapshot.Truncated,
            Entries = entries
        };
        return true;
    }

    internal static SessionArtifactEntry[] NormalizeArtifactEntries(IEnumerable<SessionArtifactEntry>? entries)
    {
        if (entries is null)
        {
            return Array.Empty<SessionArtifactEntry>();
        }

        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new SessionArtifactEntry
            {
                Name = entry.Name.Trim(),
                RootAlias = entry.RootAlias?.Trim() ?? string.Empty,
                RelativePath = SessionArtifactPathNormalizer.Normalize(entry.RelativePath),
                SnapshotRelativePath = SessionArtifactPathNormalizer.Normalize(entry.SnapshotRelativePath),
                Kind = string.IsNullOrWhiteSpace(entry.Kind) ? "file" : entry.Kind.Trim(),
                SizeBytes = Math.Max(0, entry.SizeBytes),
                FileExtension = entry.FileExtension?.Trim() ?? string.Empty,
                MimeType = entry.MimeType?.Trim() ?? string.Empty,
                LastModifiedUtc = entry.LastModifiedUtc?.Trim() ?? string.Empty,
                ArchiveRelativePath = SessionArtifactPathNormalizer.Normalize(entry.ArchiveRelativePath)
            })
            .OrderByDescending(entry => IsArtifactDirectory(entry.Kind))
            .ThenBy(entry => entry.SnapshotRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsArtifactDirectory(string? kind)
        => string.Equals(kind, "directory", StringComparison.OrdinalIgnoreCase);

    internal static bool TryNormalizeVisualTreeSnapshot(
        SessionVisualTreeSnapshot snapshot,
        out SessionVisualTreeSnapshot? normalizedSnapshot)
    {
        normalizedSnapshot = null;
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId)
            || snapshot.Payload.Count == 0)
        {
            return false;
        }

        var payloadKind = LiveUiNodeQuery.ReadString(snapshot.Payload, "treeKind");
        var payloadSource = LiveUiNodeQuery.ReadString(snapshot.Payload, "source");
        var payloadFormat = LiveUiNodeQuery.ReadString(snapshot.Payload, "format");
        var payloadPlatform = LiveUiNodeQuery.ReadString(snapshot.Payload, "platform");
        var payloadRootScope = LiveUiNodeQuery.ReadString(snapshot.Payload, "rootScope");
        normalizedSnapshot = new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshot.SnapshotId.Trim(),
            CapturedAtUtc = snapshot.CapturedAtUtc.ToUniversalTime(),
            VisualTreeKind = VisualTreeContract.NormalizeKind(
                toolId: null,
                payloadKind ?? snapshot.VisualTreeKind,
                payloadSource,
                payloadFormat ?? snapshot.VisualTreeFormat),
            VisualTreeFormat = VisualTreeContract.NormalizeFormat(payloadFormat ?? snapshot.VisualTreeFormat),
            RuntimePlatform = VisualTreeContract.NormalizeRuntimePlatform(payloadPlatform ?? snapshot.RuntimePlatform),
            Source = string.IsNullOrWhiteSpace(snapshot.Source) ? "ansight.visualTree" : snapshot.Source.Trim(),
            RootScope = VisualTreeContract.NormalizeRootScope(payloadRootScope ?? snapshot.RootScope),
            MaxDepth = Math.Max(0, snapshot.MaxDepth),
            IncludeProperties = snapshot.IncludeProperties,
            IncludeBindableProperties = snapshot.IncludeBindableProperties,
            NodeCount = Math.Max(0, snapshot.NodeCount),
            Truncated = snapshot.Truncated,
            ScreenshotFrameId = string.IsNullOrWhiteSpace(snapshot.ScreenshotFrameId) ? null : snapshot.ScreenshotFrameId.Trim(),
            ScreenshotCapturedAtUtc = snapshot.ScreenshotCapturedAtUtc?.ToUniversalTime(),
            ActionId = string.IsNullOrWhiteSpace(snapshot.ActionId) ? null : snapshot.ActionId.Trim(),
            ActionCapability = string.IsNullOrWhiteSpace(snapshot.ActionCapability) ? null : snapshot.ActionCapability.Trim(),
            EvidencePhase = string.IsNullOrWhiteSpace(snapshot.EvidencePhase) ? null : snapshot.EvidencePhase.Trim(),
            TreeHash = string.IsNullOrWhiteSpace(snapshot.TreeHash) ? null : snapshot.TreeHash.Trim(),
            ScreenshotHash = string.IsNullOrWhiteSpace(snapshot.ScreenshotHash) ? null : snapshot.ScreenshotHash.Trim(),
            Payload = snapshot.Payload.DeepClone() as JsonObject ?? new JsonObject()
        };
        return true;
    }

    internal static SessionAnnotationGeometry[] NormalizeGeometry(IEnumerable<SessionAnnotationGeometry>? geometry)
    {
        if (geometry is null)
        {
            return Array.Empty<SessionAnnotationGeometry>();
        }

        var normalized = new List<SessionAnnotationGeometry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var current in geometry)
        {
            if (string.IsNullOrWhiteSpace(current.GeometryId)
                || string.IsNullOrWhiteSpace(current.FrameId))
            {
                continue;
            }

            var normalizedCurrent = NormalizeGeometry(current);
            if (normalizedCurrent is null || !seenIds.Add(normalizedCurrent.GeometryId))
            {
                continue;
            }

            normalized.Add(normalizedCurrent);
        }

        return normalized.ToArray();
    }

    internal static SessionAnnotationEvidence[] NormalizeAnnotationEvidence(
        IEnumerable<SessionAnnotationEvidence>? evidence)
    {
        return evidence?
            .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                           && !string.IsNullOrWhiteSpace(item.Kind)
                           && !string.IsNullOrWhiteSpace(item.Status))
            .Select(item => new SessionAnnotationEvidence
            {
                Id = item.Id.Trim(),
                Kind = item.Kind.Trim(),
                Status = item.Status.Trim(),
                Reason = RuntimeState.NormalizeNotes(item.Reason),
                CapturedAtUtc = item.CapturedAtUtc?.ToUniversalTime(),
                SizeBytes = item.SizeBytes.HasValue ? Math.Max(0, item.SizeBytes.Value) : null,
                Truncated = item.Truncated
            })
            .ToArray()
            ?? Array.Empty<SessionAnnotationEvidence>();
    }

    internal static SessionAnnotationGeometry? NormalizeGeometry(SessionAnnotationGeometry geometry)
    {
        if (geometry.Kind is SessionAnnotationGeometryKind.FreeDraw
            or SessionAnnotationGeometryKind.Line
            or SessionAnnotationGeometryKind.Arrow)
        {
            var points = geometry.Points
                .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                .Select(point => new SessionAnnotationGeometryPoint
                {
                    X = Math.Clamp(point.X, 0d, 1d),
                    Y = Math.Clamp(point.Y, 0d, 1d)
                })
                .ToArray();
            if (points.Length < 2)
            {
                return null;
            }

            var pathX = points.Min(point => point.X);
            var pathY = points.Min(point => point.Y);
            var pathWidth = points.Max(point => point.X) - pathX;
            var pathHeight = points.Max(point => point.Y) - pathY;
            if (pathWidth <= 0d && pathHeight <= 0d)
            {
                return null;
            }

            return new SessionAnnotationGeometry
            {
                GeometryId = geometry.GeometryId.Trim(),
                FrameId = geometry.FrameId.Trim(),
                CapturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime(),
                Kind = geometry.Kind,
                X = pathX,
                Y = pathY,
                Width = pathWidth,
                Height = pathHeight,
                Points = points,
                Text = RuntimeState.NormalizeNotes(geometry.Text),
                StrokeColor = RuntimeState.NormalizeNotes(geometry.StrokeColor),
                StrokeWidth = geometry.StrokeWidth.HasValue ? Math.Max(0d, geometry.StrokeWidth.Value) : null
            };
        }

        var x = Math.Clamp(geometry.X, 0d, 1d);
        var y = Math.Clamp(geometry.Y, 0d, 1d);

        return geometry.Kind switch
        {
            SessionAnnotationGeometryKind.Point => new SessionAnnotationGeometry
            {
                GeometryId = geometry.GeometryId.Trim(),
                FrameId = geometry.FrameId.Trim(),
                CapturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime(),
                Kind = SessionAnnotationGeometryKind.Point,
                X = x,
                Y = y,
                Text = RuntimeState.NormalizeNotes(geometry.Text),
                StrokeColor = RuntimeState.NormalizeNotes(geometry.StrokeColor),
                StrokeWidth = geometry.StrokeWidth.HasValue ? Math.Max(0d, geometry.StrokeWidth.Value) : null
            },
            SessionAnnotationGeometryKind.Rectangle or SessionAnnotationGeometryKind.Ellipse
                when geometry.Width.GetValueOrDefault() > 0d && geometry.Height.GetValueOrDefault() > 0d => new SessionAnnotationGeometry
            {
                GeometryId = geometry.GeometryId.Trim(),
                FrameId = geometry.FrameId.Trim(),
                CapturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime(),
                Kind = geometry.Kind,
                X = x,
                Y = y,
                Width = Math.Clamp(geometry.Width!.Value, 0d, 1d - x),
                Height = Math.Clamp(geometry.Height!.Value, 0d, 1d - y),
                Text = RuntimeState.NormalizeNotes(geometry.Text),
                StrokeColor = RuntimeState.NormalizeNotes(geometry.StrokeColor),
                StrokeWidth = geometry.StrokeWidth.HasValue ? Math.Max(0d, geometry.StrokeWidth.Value) : null
            },
            _ => null
        };
    }

    internal static bool SessionAnnotationsEqual(SessionAnnotation left, SessionAnnotation right)
    {
        if (!string.Equals(left.AnnotationId, right.AnnotationId, StringComparison.Ordinal)
            || !string.Equals(left.Label, right.Label, StringComparison.Ordinal)
            || !string.Equals(left.Source, right.Source, StringComparison.Ordinal)
            || !string.Equals(left.Notes, right.Notes, StringComparison.Ordinal)
            || !string.Equals(left.Status, right.Status, StringComparison.Ordinal)
            || !string.Equals(left.CaptureGroupId, right.CaptureGroupId, StringComparison.Ordinal)
            || !JsonNode.DeepEquals(left.CustomData, right.CustomData)
            || !left.HookFailures.SequenceEqual(right.HookFailures, StringComparer.Ordinal)
            || !AnnotationEvidenceEqual(left.Evidence, right.Evidence)
            || left.StartUtc != right.StartUtc
            || left.EndUtc != right.EndUtc
            || !SessionAnnotationTargetEqual(left.Target, right.Target)
            || left.Geometry.Count != right.Geometry.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Geometry.Count; index++)
        {
            if (!SessionAnnotationGeometryEqual(left.Geometry[index], right.Geometry[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AnnotationEvidenceEqual(
        IReadOnlyList<SessionAnnotationEvidence> left,
        IReadOnlyList<SessionAnnotationEvidence> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal)
                || !string.Equals(left[index].Kind, right[index].Kind, StringComparison.Ordinal)
                || !string.Equals(left[index].Status, right[index].Status, StringComparison.Ordinal)
                || !string.Equals(left[index].Reason, right[index].Reason, StringComparison.Ordinal)
                || left[index].CapturedAtUtc != right[index].CapturedAtUtc
                || left[index].SizeBytes != right[index].SizeBytes
                || left[index].Truncated != right[index].Truncated)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool SessionAnnotationTargetEqual(SessionAnnotationTarget? left, SessionAnnotationTarget? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
               && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
               && string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
               && string.Equals(left.VisualTreeSnapshotId, right.VisualTreeSnapshotId, StringComparison.Ordinal)
               && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
               && string.Equals(left.ElementKind, right.ElementKind, StringComparison.Ordinal)
               && string.Equals(left.Label, right.Label, StringComparison.Ordinal)
               && string.Equals(left.AutomationId, right.AutomationId, StringComparison.Ordinal)
               && left.Depth == right.Depth
               && left.ChildCount == right.ChildCount
               && SessionAnnotationTargetBoundsEqual(left.AbsoluteBounds, right.AbsoluteBounds)
               && SessionAnnotationTargetBoundsEqual(left.NormalizedBounds, right.NormalizedBounds);
    }

    internal static bool SessionAnnotationTargetBoundsEqual(
        SessionAnnotationTargetBounds? left,
        SessionAnnotationTargetBounds? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.X.Equals(right.X)
               && left.Y.Equals(right.Y)
               && left.Width.Equals(right.Width)
               && left.Height.Equals(right.Height);
    }

    internal static bool SessionAnnotationGeometryEqual(SessionAnnotationGeometry left, SessionAnnotationGeometry right)
    {
        return string.Equals(left.GeometryId, right.GeometryId, StringComparison.Ordinal)
               && string.Equals(left.FrameId, right.FrameId, StringComparison.Ordinal)
               && left.CapturedAtUtc == right.CapturedAtUtc
               && left.Kind == right.Kind
               && left.X.Equals(right.X)
               && left.Y.Equals(right.Y)
               && Nullable.Equals(left.Width, right.Width)
               && Nullable.Equals(left.Height, right.Height)
               && GeometryPointsEqual(left.Points, right.Points)
               && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
               && string.Equals(left.StrokeColor, right.StrokeColor, StringComparison.Ordinal)
               && Nullable.Equals(left.StrokeWidth, right.StrokeWidth);
    }

    internal static bool GeometryPointsEqual(
        IReadOnlyList<SessionAnnotationGeometryPoint> left,
        IReadOnlyList<SessionAnnotationGeometryPoint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].X.Equals(right[index].X) || !left[index].Y.Equals(right[index].Y))
            {
                return false;
            }
        }

        return true;
    }

    internal static int CompareVisualTreeSnapshots(SessionVisualTreeSnapshot? left, SessionVisualTreeSnapshot? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var capturedComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        return capturedComparison != 0
            ? capturedComparison
            : string.CompareOrdinal(left.SnapshotId, right.SnapshotId);
    }

    internal static int CompareArtifactSnapshots(SessionArtifactSnapshot? left, SessionArtifactSnapshot? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var capturedComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        return capturedComparison != 0
            ? capturedComparison
            : string.CompareOrdinal(left.SnapshotId, right.SnapshotId);
    }

    internal static int CompareAnnotations(SessionAnnotation? left, SessionAnnotation? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var startComparison = left.StartUtc.CompareTo(right.StartUtc);
        if (startComparison != 0)
        {
            return startComparison;
        }

        var leftEnd = left.EndUtc ?? left.StartUtc;
        var rightEnd = right.EndUtc ?? right.StartUtc;
        var endComparison = leftEnd.CompareTo(rightEnd);
        if (endComparison != 0)
        {
            return endComparison;
        }

        return string.Compare(left.AnnotationId, right.AnnotationId, StringComparison.Ordinal);
    }

    internal static string[] NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var rawTag in tags)
        {
            foreach (var tag in SplitTags(rawTag))
            {
                if (seen.Add(tag))
                {
                    normalized.Add(tag);
                }
            }
        }

        return normalized.ToArray();
    }

    internal static IEnumerable<string> SplitTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            yield break;
        }

        foreach (var tag in rawTags.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                yield return tag;
            }
        }
    }
}
