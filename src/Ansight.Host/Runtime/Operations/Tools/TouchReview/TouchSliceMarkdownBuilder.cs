using System.Text;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchSliceMarkdownBuilder
{
    public static string BuildMarkdownTouchSlice(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionTouchInputRecord> touches,
        IReadOnlyList<TouchGestureSegment> gestures,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Touch slice: {snapshot.AppId}");
        builder.AppendLine();
        builder.AppendLine($"Session: `{snapshot.SessionId}`");
        builder.AppendLine($"Window: {startUtc:O} to {endUtc:O}");
        builder.AppendLine($"Touches: {touches.Count}");
        builder.AppendLine($"Gestures: {gestures.Count}");
        builder.AppendLine();
        builder.AppendLine("## Gestures");
        foreach (var gesture in gestures.Take(25))
        {
            builder.AppendLine($"- `{gesture.GestureId}` {gesture.Kind} at {gesture.StartUtc:O}, {gesture.Touches.Count} touches, {gesture.DurationMilliseconds} ms");
        }

        builder.AppendLine();
        builder.AppendLine("## Touches");
        foreach (var touch in touches.Take(50))
        {
            var point = TouchReviewGeometry.ResolveTouchPoint(touch);
            var pointText = point.HasValue ? $"{point.Value.X:0.###}, {point.Value.Y:0.###}" : "unresolved";
            builder.AppendLine($"- {touch.CapturedAtUtc:O} {TouchReviewGeometry.NormalizeTouchAction(touch.Action)} pointer {touch.PointerId} at {pointText}");
        }

        return builder.ToString();
    }
}
