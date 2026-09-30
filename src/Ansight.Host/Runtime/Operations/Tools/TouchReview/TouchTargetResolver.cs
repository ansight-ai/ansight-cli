using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchTargetResolver
{
    public static bool TryResolveTouchTarget(
        JsonObject? arguments,
        IReadOnlyList<SessionTouchInputRecord> touches,
        out int targetIndex,
        out string targetSelector,
        out string? errorMessage)
    {
        targetIndex = -1;
        targetSelector = string.Empty;
        errorMessage = null;

        var touchId = NormalizeOptionalString(arguments?["touchId"]?.GetValue<string>());
        if (touchId is not null)
        {
            for (var index = 0; index < touches.Count; index++)
            {
                if (string.Equals(touches[index].Id, touchId, StringComparison.Ordinal))
                {
                    targetIndex = index;
                    targetSelector = "touchId";
                    return true;
                }
            }

            errorMessage = $"No touch with id '{touchId}' was found.";
            return false;
        }

        if (!TouchReviewArgumentReader.TryReadOptionalIntegerArgument(arguments, "touchIndex", out var touchIndex, out errorMessage))
        {
            return false;
        }

        if (touchIndex.HasValue)
        {
            if (touchIndex.Value < 0 || touchIndex.Value >= touches.Count)
            {
                errorMessage = $"touchIndex must be between 0 and {touches.Count - 1}.";
                return false;
            }

            targetIndex = touchIndex.Value;
            targetSelector = "touchIndex";
            return true;
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage))
        {
            return false;
        }

        if (!timestampUtc.HasValue)
        {
            errorMessage = "One of touchId, touchIndex, or timestampUtc is required.";
            return false;
        }

        targetIndex = ResolveNearestTouchIndex(touches, timestampUtc.Value.ToUniversalTime());
        targetSelector = "timestampUtc";
        return targetIndex >= 0;
    }

    public static bool TryResolveTouchArtifactTarget(
        JsonObject? arguments,
        IReadOnlyList<SessionTouchInputRecord> touches,
        out DateTimeOffset targetStartUtc,
        out DateTimeOffset targetEndUtc,
        out JsonObject targetPayload,
        out string? errorMessage)
    {
        targetStartUtc = default;
        targetEndUtc = default;
        targetPayload = new JsonObject();
        errorMessage = null;

        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds));
        var gestureId = NormalizeOptionalString(arguments?["gestureId"]?.GetValue<string>());
        if (gestureId is not null)
        {
            var gesture = gestures.FirstOrDefault(candidate => string.Equals(candidate.GestureId, gestureId, StringComparison.Ordinal));
            if (gesture is null)
            {
                errorMessage = $"No gesture with id '{gestureId}' was found.";
                return false;
            }

            targetStartUtc = gesture.StartUtc;
            targetEndUtc = gesture.EndUtc;
            targetPayload = new JsonObject
            {
                ["kind"] = "gesture",
                ["gesture"] = TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: false)
            };
            return true;
        }

        if (TryResolveTouchTarget(arguments, touches, out var targetIndex, out var targetSelector, out errorMessage))
        {
            var touch = touches[targetIndex];
            targetStartUtc = touch.CapturedAtUtc;
            targetEndUtc = touch.CapturedAtUtc;
            targetPayload = new JsonObject
            {
                ["kind"] = "touch",
                ["targetSelector"] = targetSelector,
                ["touch"] = TouchReviewPayloads.BuildTouchPayload(touch, targetIndex, isTarget: true)
            };
            return true;
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage))
        {
            return false;
        }

        if (!timestampUtc.HasValue)
        {
            errorMessage = "One of gestureId, touchId, touchIndex, or timestampUtc is required.";
            return false;
        }

        targetStartUtc = timestampUtc.Value.ToUniversalTime();
        targetEndUtc = targetStartUtc;
        targetPayload = new JsonObject
        {
            ["kind"] = "timestamp",
            ["timestampUtc"] = targetStartUtc
        };
        return true;
    }

    private static int ResolveNearestTouchIndex(IReadOnlyList<SessionTouchInputRecord> touches, DateTimeOffset timestampUtc)
    {
        var nearestIndex = -1;
        var nearestDistance = TimeSpan.MaxValue;
        for (var index = 0; index < touches.Count; index++)
        {
            var distance = (touches[index].CapturedAtUtc - timestampUtc).Duration();
            if (distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
