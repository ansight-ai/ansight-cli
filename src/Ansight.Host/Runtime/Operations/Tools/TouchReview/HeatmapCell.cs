using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class HeatmapCell
{
    private readonly Dictionary<string, int> actionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> pointerIds = new();

    public HeatmapCell(int column, int row)
    {
        Column = column;
        Row = row;
    }

    public int Column { get; }

    public int Row { get; }

    public int TouchCount { get; private set; }

    public void Add(SessionTouchInputRecord touch)
    {
        TouchCount++;
        pointerIds.Add(touch.PointerId);
        var action = TouchReviewGeometry.NormalizeTouchAction(touch.Action);
        actionCounts[action] = actionCounts.GetValueOrDefault(action) + 1;
    }

    public JsonObject ToPayload(int columns, int rows)
    {
        return new JsonObject
        {
            ["column"] = Column,
            ["row"] = Row,
            ["xStart"] = Column / (double)columns,
            ["xEnd"] = (Column + 1) / (double)columns,
            ["yStart"] = Row / (double)rows,
            ["yEnd"] = (Row + 1) / (double)rows,
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
