using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchHeatmapBuilder
{
    public static JsonArray BuildHeatmapCells(
        IReadOnlyList<SessionTouchInputRecord> touches,
        int columns,
        int rows,
        bool includeEmpty)
    {
        var cells = new HeatmapCell[columns, rows];
        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                cells[column, row] = new HeatmapCell(column, row);
            }
        }

        foreach (var touch in touches)
        {
            var point = TouchReviewGeometry.ResolveTouchPoint(touch);
            if (point is null)
            {
                continue;
            }

            var column = Math.Clamp((int)Math.Floor(point.Value.X * columns), 0, columns - 1);
            var row = Math.Clamp((int)Math.Floor(point.Value.Y * rows), 0, rows - 1);
            cells[column, row].Add(touch);
        }

        var payloads = new List<JsonNode?>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var cell = cells[column, row];
                if (includeEmpty || cell.TouchCount > 0)
                {
                    payloads.Add(cell.ToPayload(columns, rows));
                }
            }
        }

        return PayloadJson.CreateJsonArray(payloads);
    }
}
