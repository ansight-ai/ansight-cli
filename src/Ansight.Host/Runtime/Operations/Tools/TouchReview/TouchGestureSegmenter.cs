namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchGestureSegmenter
{
    public static IReadOnlyList<TouchGestureSegment> BuildGestureSegments(
        IReadOnlyList<SessionTouchInputRecord> touches,
        TimeSpan gestureGap)
    {
        var segments = new List<TouchGestureSegment>();
        foreach (var pointerGroup in touches.GroupBy(touch => touch.PointerId).OrderBy(group => group.Key))
        {
            var pointerTouches = pointerGroup
                .OrderBy(touch => touch.CapturedAtUtc)
                .ThenBy(touch => touch.Id, StringComparer.Ordinal)
                .ToArray();
            var currentTouches = new List<SessionTouchInputRecord>();
            DateTimeOffset? lastTouchUtc = null;

            foreach (var touch in pointerTouches)
            {
                var shouldStartNewSegment = currentTouches.Count == 0
                                            || TouchReviewGeometry.IsTouchDown(touch.Action)
                                            || (lastTouchUtc.HasValue && touch.CapturedAtUtc - lastTouchUtc.Value > gestureGap);
                if (shouldStartNewSegment && currentTouches.Count > 0)
                {
                    segments.Add(CreateGestureSegment(currentTouches));
                    currentTouches.Clear();
                }

                currentTouches.Add(touch);
                lastTouchUtc = touch.CapturedAtUtc;
                if (TouchReviewGeometry.IsTouchEnd(touch.Action))
                {
                    segments.Add(CreateGestureSegment(currentTouches));
                    currentTouches.Clear();
                    lastTouchUtc = null;
                }
            }

            if (currentTouches.Count > 0)
            {
                segments.Add(CreateGestureSegment(currentTouches));
            }
        }

        return segments
            .OrderBy(segment => segment.StartUtc)
            .ThenBy(segment => segment.PointerIds[0])
            .Select((segment, index) => segment with
            {
                GestureId = $"gesture-{index + 1:000}-{segment.StartUtc.UtcTicks:x}"
            })
            .ToArray();
    }

    private static TouchGestureSegment CreateGestureSegment(IReadOnlyList<SessionTouchInputRecord> touches)
    {
        var orderedTouches = touches
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
        var startTouch = orderedTouches[0];
        var endTouch = orderedTouches[^1];
        var startPoint = TouchReviewGeometry.ResolveTouchPoint(startTouch);
        var endPoint = TouchReviewGeometry.ResolveTouchPoint(endTouch);
        var distance = startPoint.HasValue && endPoint.HasValue
            ? TouchReviewGeometry.CalculateDistance(startPoint.Value, endPoint.Value)
            : (double?)null;
        var maxDistance = startPoint.HasValue
            ? orderedTouches
                .Select(TouchReviewGeometry.ResolveTouchPoint)
                .Where(point => point.HasValue)
                .Select(point => TouchReviewGeometry.CalculateDistance(startPoint.Value, point!.Value))
                .DefaultIfEmpty(0d)
                .Max()
            : (double?)null;
        var isComplete = TouchReviewGeometry.IsTouchEnd(endTouch.Action);
        var durationMs = Math.Max(0, (long)Math.Round((endTouch.CapturedAtUtc - startTouch.CapturedAtUtc).TotalMilliseconds));
        var kind = ClassifyGesture(orderedTouches, durationMs, distance, maxDistance, isComplete);
        return new TouchGestureSegment(
            string.Empty,
            kind,
            orderedTouches.Select(touch => touch.PointerId).Distinct().OrderBy(pointerId => pointerId).ToArray(),
            orderedTouches,
            startTouch.CapturedAtUtc,
            endTouch.CapturedAtUtc,
            durationMs,
            distance,
            maxDistance,
            isComplete);
    }

    private static string ClassifyGesture(
        IReadOnlyList<SessionTouchInputRecord> touches,
        long durationMilliseconds,
        double? distance,
        double? maxDistance,
        bool isComplete)
    {
        if (touches.Any(touch => touch.PointerCount > 1))
        {
            return "multiTouch";
        }

        if (!isComplete)
        {
            return "incomplete";
        }

        var resolvedMaxDistance = maxDistance ?? distance ?? 0d;
        if (resolvedMaxDistance <= TouchReviewDefaults.TapMaximumNormalizedDistance)
        {
            return durationMilliseconds >= TouchReviewDefaults.LongPressMinimumMilliseconds ? "longPress" : "tap";
        }

        return "drag";
    }
}
