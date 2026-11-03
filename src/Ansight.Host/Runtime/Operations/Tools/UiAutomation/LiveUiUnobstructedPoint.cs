using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiUnobstructedPoint
{
    public static LiveUiScreenPoint? Resolve(
        LiveUiTreeCapture capture, LiveUiNodeMatch target, LiveUiScreenPoint requested, bool explicitPoint)
    {
        var area = LiveUiNodeQuery.ReadBounds(target.Node);
        if (area is null || capture.Viewport is null)
        {
            return null;
        }

        area = LiveUiBounds.Intersection(area, capture.Viewport);
        if (area is null)
        {
            return null;
        }

        var obstacles = LiveUiNodeQuery.Enumerate(capture.Root, capture.TypeRegistry)
            .Where(candidate => IsCompetingControl(candidate, target)
                && !VisualTreePresentationNormalizer.IsBehind(capture.Payload, candidate, target))
            .Select(candidate => LiveUiNodeQuery.ReadBounds(candidate.Node))
            .OfType<LiveUiBounds>()
            .ToArray();
        if (!obstacles.Any(bounds => Contains(bounds, requested)))
        {
            return requested;
        }

        if (explicitPoint)
        {
            return null;
        }

        // Accessibility bounds can overlap a sibling (for example a bottom tab).
        // Choose a point strictly inside the remaining target area, never the sibling.
        foreach (var y in new[] { 0.25, 0.75, 0.1, 0.9, 0.5 })
        {
            foreach (var x in new[] { 0.5, 0.25, 0.75, 0.1, 0.9 })
            {
                var point = new LiveUiScreenPoint(area.X + area.Width * x, area.Y + area.Height * y);
                if (!obstacles.Any(bounds => Contains(bounds, point)))
                {
                    return point;
                }
            }
        }

        return null;
    }

    private static bool IsCompetingControl(LiveUiNodeMatch candidate, LiveUiNodeMatch target)
    {
        var related = SameObservedNode(candidate.Node, target.Node)
            || candidate.Ancestors.Any(ancestor => SameObservedNode(ancestor, target.Node))
            || target.Ancestors.Any(ancestor => SameObservedNode(ancestor, candidate.Node));
        if (related)
        {
            return false;
        }

        var actionable = LiveUiNodeQuery.IsEffectivelyVisible(candidate)
            && LiveUiNodeQuery.IsEffectivelyEnabled(candidate)
            && LiveUiNodeQuery.ReadSupportedActions(candidate.Node, candidate.TypeRegistry)
                .Any(action => action is "tap" or "typeText" or "longPress");
        return actionable;
    }

    private static bool SameObservedNode(JsonObject first, JsonObject second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        // A single capture may project one physical node through multiple roots.
        // Only a shared runtime ID AND geometry corroborate that identity; reused
        // automation IDs or matching labels must not hide a competing control.
        var sameProjection = LiveUiNodeQuery.ReadString(first, "id") is { } id
            && id == LiveUiNodeQuery.ReadString(second, "id")
            && LiveUiNodeQuery.ReadBounds(first) is { } bounds
            && bounds == LiveUiNodeQuery.ReadBounds(second);
        return sameProjection;
    }

    private static bool Contains(LiveUiBounds bounds, LiveUiScreenPoint point)
    {
        var contains = point.X >= bounds.X
            && point.X <= bounds.X + bounds.Width
            && point.Y >= bounds.Y
            && point.Y <= bounds.Y + bounds.Height;
        return contains;
    }
}
