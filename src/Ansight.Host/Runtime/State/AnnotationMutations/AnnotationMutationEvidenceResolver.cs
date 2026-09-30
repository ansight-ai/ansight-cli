using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.State;

internal static class AnnotationMutationEvidenceResolver
{
    public static bool TryResolveGeometries(
        SessionState session,
        IReadOnlyList<SessionAnnotationGeometry> inputs,
        out IReadOnlyList<SessionAnnotationGeometry> geometries,
        out string? error)
    {
        var resolved = new List<SessionAnnotationGeometry>();
        geometries = [];
        error = null;
        foreach (var geometry in inputs)
        {
            var frame = session.Images.FirstOrDefault(candidate =>
                string.Equals(candidate.FrameId, geometry.FrameId, StringComparison.Ordinal));
            if (frame is null)
            {
                error = $"Screenshot frame '{geometry.FrameId}' was not found in this session.";
                return false;
            }
            resolved.Add(new SessionAnnotationGeometry
            {
                GeometryId = geometry.GeometryId,
                FrameId = frame.FrameId,
                CapturedAtUtc = frame.CapturedAtUtc.ToUniversalTime(),
                Kind = geometry.Kind,
                X = geometry.X,
                Y = geometry.Y,
                Width = geometry.Width,
                Height = geometry.Height,
                Points = geometry.Points.Select(SessionSnapshotCloner.CloneAnnotationGeometryPoint).ToArray(),
                Text = geometry.Text,
                StrokeColor = geometry.StrokeColor,
                StrokeWidth = geometry.StrokeWidth
            });
        }
        geometries = resolved.OrderBy(geometry => geometry.CapturedAtUtc)
            .ThenBy(geometry => geometry.GeometryId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    public static bool TryResolveTarget(
        SessionState session,
        SessionAnnotationTargetReference reference,
        string source,
        out SessionAnnotationTarget? target,
        out string? error)
    {
        target = null;
        error = null;
        var snapshot = session.VisualTreeSnapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, reference.VisualTreeSnapshotId, StringComparison.Ordinal));
        if (snapshot?.Payload["root"] is not JsonObject root)
        {
            error = $"Visual-tree snapshot '{reference.VisualTreeSnapshotId}' was not found or has no root in this session.";
            return false;
        }

        var pending = new Stack<AnnotationTargetCandidate>();
        pending.Push(new AnnotationTargetCandidate(root, 0));
        AnnotationTargetCandidate? selected = null;
        while (pending.TryPop(out var candidate))
        {
            if (string.Equals(ReadString(candidate.Node, "id"), reference.NodeId, StringComparison.Ordinal))
            {
                if (selected is not null)
                {
                    error = $"Node '{reference.NodeId}' is ambiguous in the selected visual-tree snapshot.";
                    return false;
                }
                selected = candidate;
            }
            if (candidate.Node["children"] is JsonArray children)
            {
                foreach (var child in children.OfType<JsonObject>())
                {
                    pending.Push(new AnnotationTargetCandidate(child, candidate.Depth + 1));
                }
            }
        }
        if (selected is null)
        {
            error = $"Node '{reference.NodeId}' was not found in visual-tree snapshot '{reference.VisualTreeSnapshotId}'.";
            return false;
        }

        var node = selected.Node;
        var absolute = ReadBoundsObject(node["absoluteBounds"] as JsonObject)
                       ?? ToBounds(LiveUiNodeQuery.ReadBounds(node));
        var normalized = ReadBoundsObject(node["normalizedBounds"] as JsonObject);
        if (normalized is null && absolute is not null
            && LiveUiNodeQuery.ReadCoordinateSpace(snapshot.Payload) is { Width: > 0, Height: > 0 } viewport)
        {
            normalized = new SessionAnnotationTargetBounds
            {
                X = (absolute.X - viewport.X) / viewport.Width,
                Y = (absolute.Y - viewport.Y) / viewport.Height,
                Width = absolute.Width / viewport.Width,
                Height = absolute.Height / viewport.Height
            };
        }

        target = new SessionAnnotationTarget
        {
            Kind = "visualTreeElement",
            Source = source,
            TargetId = reference.NodeId,
            VisualTreeSnapshotId = snapshot.SnapshotId,
            Type = VisualTreeTypeRegistry.FromPayload(snapshot.Payload).Resolve(node) ?? ReadString(node, "type") ?? string.Empty,
            ElementKind = ReadString(node, "elementKind") ?? ReadString(node, "kind") ?? string.Empty,
            Label = LiveUiNodeQuery.ReadText(node) ?? string.Empty,
            AutomationId = LiveUiNodeQuery.ReadAutomationId(node) ?? string.Empty,
            Depth = selected.Depth,
            ChildCount = node["children"] is JsonArray nodeChildren ? nodeChildren.Count : ReadChildCount(node),
            AbsoluteBounds = RuntimeSnapshotNormalizer.NormalizeAnnotationTargetBounds(absolute, clampToUnitInterval: false),
            NormalizedBounds = RuntimeSnapshotNormalizer.NormalizeAnnotationTargetBounds(normalized, clampToUnitInterval: true)
        };
        return true;
    }

    private static SessionAnnotationTargetBounds? ToBounds(LiveUiBounds? bounds)
        => bounds is null ? null : new SessionAnnotationTargetBounds
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height
        };

    private static SessionAnnotationTargetBounds? ReadBoundsObject(JsonObject? bounds)
    {
        if (bounds is null
            || !TryReadNumber(bounds["x"], out var x)
            || !TryReadNumber(bounds["y"], out var y)
            || !TryReadNumber(bounds["width"], out var width)
            || !TryReadNumber(bounds["height"], out var height))
        {
            return null;
        }
        return new SessionAnnotationTargetBounds { X = x, Y = y, Width = width, Height = height };
    }

    private static bool TryReadNumber(JsonNode? node, out double number)
    {
        number = 0;
        return node is JsonValue value
               && value.GetValueKind() == JsonValueKind.Number
               && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
               && double.IsFinite(number);
    }

    private static string? ReadString(JsonObject value, string property)
        => value[property] is JsonValue text && text.TryGetValue<string>(out var parsed) ? parsed : null;

    private static int ReadChildCount(JsonObject node)
        => node["childCount"] is JsonValue value && value.TryGetValue<int>(out var count) ? Math.Max(0, count) : 0;
}
