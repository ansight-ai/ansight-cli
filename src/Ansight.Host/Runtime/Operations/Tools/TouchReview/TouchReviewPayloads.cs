using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchReviewPayloads
{
    public static JsonObject BuildGesturePayload(TouchGestureSegment gesture, bool includeTouches)
    {
        var payload = new JsonObject
        {
            ["gestureId"] = gesture.GestureId,
            ["kind"] = gesture.Kind,
            ["startUtc"] = gesture.StartUtc,
            ["endUtc"] = gesture.EndUtc,
            ["durationMs"] = gesture.DurationMilliseconds,
            ["touchCount"] = gesture.Touches.Count,
            ["pointerIds"] = PayloadJson.CreateJsonArray(gesture.PointerIds.Select(pointerId => JsonValue.Create(pointerId))),
            ["isComplete"] = gesture.IsComplete,
            ["distance"] = gesture.DistanceNormalized,
            ["maxDistance"] = gesture.MaxDistanceNormalized,
            ["startPoint"] = BuildPointPayload(TouchReviewGeometry.ResolveTouchPoint(gesture.Touches[0])),
            ["endPoint"] = BuildPointPayload(TouchReviewGeometry.ResolveTouchPoint(gesture.Touches[^1])),
            ["actionCounts"] = BuildStringCountsArray(gesture.Touches.Select(touch => TouchReviewGeometry.NormalizeTouchAction(touch.Action)))
        };

        if (includeTouches)
        {
            payload["touches"] = PayloadJson.CreateJsonArray(gesture.Touches.Select((touch, index) => (JsonNode?)BuildTouchPayload(touch, index)));
        }

        return payload;
    }

    public static JsonObject BuildTouchPayload(SessionTouchInputRecord touch, int? touchIndex = null, bool isTarget = false)
    {
        var point = TouchReviewGeometry.ResolveTouchPoint(touch);
        return new JsonObject
        {
            ["id"] = touch.Id,
            ["touchIndex"] = touchIndex,
            ["isTarget"] = isTarget,
            ["action"] = TouchReviewGeometry.NormalizeTouchAction(touch.Action),
            ["capturedAtUtc"] = touch.CapturedAtUtc,
            ["pointerId"] = touch.PointerId,
            ["pointerIndex"] = touch.PointerIndex,
            ["pointerCount"] = touch.PointerCount,
            ["x"] = touch.X,
            ["y"] = touch.Y,
            ["normalizedX"] = point?.X,
            ["normalizedY"] = point?.Y,
            ["surfaceWidth"] = touch.SurfaceWidth,
            ["surfaceHeight"] = touch.SurfaceHeight,
            ["surfaceScale"] = touch.SurfaceScale,
            ["coordinateSpace"] = touch.CoordinateSpace,
            ["coordinateUnit"] = touch.CoordinateUnit,
            ["details"] = touch.Details is null
                ? null
                : JsonSerializer.SerializeToNode(touch.Details, JsonUtil.Compact)
        };
    }

    public static JsonObject BuildPointPayload(NormalizedPoint? point)
    {
        return point is null
            ? new JsonObject()
            : new JsonObject
            {
                ["x"] = point.Value.X,
                ["y"] = point.Value.Y
            };
    }

    public static JsonArray BuildStringCountsArray(IEnumerable<string?> values, int limit = 20)
    {
        return PayloadJson.CreateJsonArray(values
            .Select(value => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(group => (JsonNode?)new JsonObject
            {
                ["value"] = group.Key,
                ["count"] = group.Count()
            }));
    }

    public static JsonArray BuildIntegerCountsArray(IEnumerable<int> values)
    {
        return PayloadJson.CreateJsonArray(values
            .GroupBy(value => value)
            .OrderBy(group => group.Key)
            .Select(group => (JsonNode?)new JsonObject
            {
                ["value"] = group.Key,
                ["count"] = group.Count()
            }));
    }
}
