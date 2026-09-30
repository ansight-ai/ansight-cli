using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class TouchTimelineBucket
{
    private readonly Dictionary<string, int> actionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> pointerIds = new();

    public TouchTimelineBucket(int index, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        Index = index;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public int Index { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public int TouchCount { get; private set; }

    public void Add(SessionTouchInputRecord touch)
    {
        TouchCount++;
        pointerIds.Add(touch.PointerId);
        var action = TouchReviewGeometry.NormalizeTouchAction(touch.Action);
        actionCounts[action] = actionCounts.GetValueOrDefault(action) + 1;
    }

    public JsonObject ToPayload()
    {
        return new JsonObject
        {
            ["index"] = Index,
            ["startUtc"] = StartUtc,
            ["endUtc"] = EndUtc,
            ["touchCount"] = TouchCount,
            ["uniquePointerCount"] = pointerIds.Count,
            ["actionCounts"] = PayloadJson.CreateJsonArray(actionCounts
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (JsonNode?)new JsonObject
                {
                    ["action"] = entry.Key,
                    ["count"] = entry.Value
                }))
        };
    }
}
