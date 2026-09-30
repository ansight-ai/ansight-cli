using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchReviewFiltering
{
    public static IReadOnlyList<SessionTouchInputRecord> GetFilteredTouches(AppSessionSnapshot snapshot, TouchFilters filters)
    {
        return snapshot.Touches
            .Where(touch => PayloadJson.MatchesTimestamp(touch.CapturedAtUtc, filters.StartUtc, filters.EndUtc))
            .Where(touch => filters.Actions.Count == 0 || filters.Actions.Contains(TouchReviewGeometry.NormalizeTouchAction(touch.Action)))
            .Where(touch => filters.PointerIds.Count == 0 || filters.PointerIds.Contains(touch.PointerId))
            .Where(touch => !filters.MinPointerCount.HasValue || touch.PointerCount >= filters.MinPointerCount.Value)
            .Where(touch => !filters.MaxPointerCount.HasValue || touch.PointerCount <= filters.MaxPointerCount.Value)
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.PointerIndex)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static TouchTimeRange ResolveTouchRange(AppSessionSnapshot snapshot, TouchFilters filters, IReadOnlyList<SessionTouchInputRecord> touches)
    {
        var startUtc = filters.StartUtc
                       ?? (touches.Count > 0 ? touches[0].CapturedAtUtc : snapshot.CreatedUtc).ToUniversalTime();
        var endUtc = filters.EndUtc
                     ?? (touches.Count > 0 ? touches[^1].CapturedAtUtc : snapshot.LastUpdatedUtc).ToUniversalTime();
        if (endUtc < startUtc)
        {
            (startUtc, endUtc) = (endUtc, startUtc);
        }

        if (endUtc <= startUtc)
        {
            endUtc = startUtc.AddMilliseconds(1);
        }

        return new TouchTimeRange(startUtc, endUtc);
    }

    public static JsonObject BuildTouchFiltersPayload(TouchFilters filters)
    {
        return new JsonObject
        {
            ["startUtc"] = filters.StartUtc,
            ["endUtc"] = filters.EndUtc,
            ["actions"] = PayloadJson.CreateJsonArray(filters.Actions.OrderBy(action => action, StringComparer.Ordinal).Select(action => JsonValue.Create(action))),
            ["pointerIds"] = PayloadJson.CreateJsonArray(filters.PointerIds.OrderBy(pointerId => pointerId).Select(pointerId => JsonValue.Create(pointerId))),
            ["minPointerCount"] = filters.MinPointerCount,
            ["maxPointerCount"] = filters.MaxPointerCount
        };
    }
}
