namespace Ansight.Host.Runtime.State;

using System.Globalization;
using System.Text.Json;
using Ansight.Host;

internal static class SessionTouchPacking
{
    public const string SchemaName = "ansight.touches.v1";
    public const string WindowSpaceCode = "w";
    public const string PixelUnitCode = "px";

    private const string PointUnitCode = "pt";
    private const string NormalizedUnitCode = "n";
    private const int ActionDown = 0;
    private const int ActionMove = 1;
    private const int ActionUp = 2;
    private const int ActionCancel = 3;
    private const int ActionUnknown = 4;
    private const int ActionHoverEnter = 5;
    private const int ActionHoverMove = 6;
    private const int ActionHoverExit = 7;

    public static List<SessionTouchPackedBatch> Pack(IReadOnlyList<SessionTouchInputRecord> touches)
    {
        if (touches.Count == 0)
        {
            return [];
        }

        return touches
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .GroupBy(CreateBatchKey)
            .Select(CreateBatch)
            .ToList();
    }

    public static IReadOnlyList<SessionTouchInputRecord> Unpack(IReadOnlyList<SessionTouchPackedBatch>? batches)
    {
        if (batches is null || batches.Count == 0)
        {
            return Array.Empty<SessionTouchInputRecord>();
        }

        var touches = new List<SessionTouchInputRecord>();
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var batch = batches[batchIndex];
            var t0 = batch.T0.ToUniversalTime();
            var surfaceWidth = ReadSurfaceValue(batch.Surface, 0);
            var surfaceHeight = ReadSurfaceValue(batch.Surface, 1);
            var surfaceScale = ReadSurfaceValue(batch.Surface, 2);
            var coordinateSpace = DecodeSpace(batch.Space);
            var coordinateUnit = DecodeUnit(batch.Unit);

            for (var rowIndex = 0; rowIndex < batch.Rows.Count; rowIndex++)
            {
                var row = batch.Rows[rowIndex];
                if (!TryReadLong(row, 0, out var deltaMs)
                    || !TryReadInt(row, 1, out var action)
                    || !TryReadLong(row, 2, out var pointerId)
                    || !TryReadDouble(row, 3, out var x)
                    || !TryReadDouble(row, 4, out var y))
                {
                    continue;
                }

                var pointerIndex = TryReadInt(row, 5, out var parsedPointerIndex)
                    ? Math.Max(0, parsedPointerIndex)
                    : 0;
                var pointerCount = TryReadInt(row, 6, out var parsedPointerCount)
                    ? Math.Max(pointerIndex + 1, parsedPointerCount)
                    : 1;
                var capturedAtUtc = t0.AddMilliseconds(Math.Max(0, deltaMs));
                touches.Add(new SessionTouchInputRecord
                {
                    Id = rowIndex < batch.Ids.Count && !string.IsNullOrWhiteSpace(batch.Ids[rowIndex])
                        ? batch.Ids[rowIndex].Trim()
                        : BuildTouchId(t0, batchIndex, rowIndex, pointerId, deltaMs),
                    Action = DecodeAction(action),
                    CapturedAtUtc = capturedAtUtc,
                    PointerId = pointerId,
                    PointerIndex = pointerIndex,
                    PointerCount = pointerCount,
                    X = x,
                    Y = y,
                    NormalizedX = ResolveNormalizedCoordinate(x, surfaceWidth, coordinateUnit),
                    NormalizedY = ResolveNormalizedCoordinate(y, surfaceHeight, coordinateUnit),
                    SurfaceWidth = surfaceWidth,
                    SurfaceHeight = surfaceHeight,
                    CoordinateSpace = coordinateSpace,
                    CoordinateUnit = coordinateUnit,
                    SurfaceScale = surfaceScale,
                    Details = ReadDetails(row)
                });
            }
        }

        return touches
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static SessionTouchBatchKey CreateBatchKey(SessionTouchInputRecord touch)
    {
        return new SessionTouchBatchKey(
            EncodeSpace(touch.CoordinateSpace),
            EncodeUnit(touch.CoordinateUnit),
            touch.SurfaceWidth,
            touch.SurfaceHeight,
            touch.SurfaceScale);
    }

    private static SessionTouchPackedBatch CreateBatch(IGrouping<SessionTouchBatchKey, SessionTouchInputRecord> group)
    {
        var touches = group
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
        var t0 = touches[0].CapturedAtUtc.ToUniversalTime();
        var rows = new List<List<object?>>(touches.Length);
        foreach (var touch in touches)
        {
            var deltaMs = (long)Math.Round(
                (touch.CapturedAtUtc.ToUniversalTime() - t0).TotalMilliseconds,
                MidpointRounding.AwayFromZero);
            var row = new List<object?>
            {
                Math.Max(0L, deltaMs),
                EncodeAction(touch.Action),
                touch.PointerId,
                touch.X,
                touch.Y
            };
            if (touch.PointerIndex != 0 || touch.PointerCount != 1 || touch.Details is not null)
            {
                row.Add(touch.PointerIndex);
                row.Add(touch.PointerCount);
            }

            if (touch.Details is not null)
            {
                row.Add(touch.Details);
            }

            rows.Add(row);
        }

        return new SessionTouchPackedBatch
        {
            T0 = t0,
            Space = group.Key.Space,
            Unit = group.Key.Unit,
            Surface =
            [
                group.Key.SurfaceWidth,
                group.Key.SurfaceHeight,
                group.Key.SurfaceScale
            ],
            Ids = touches.Select(touch => touch.Id).ToList(),
            Rows = rows
        };
    }

    private static string EncodeSpace(string? coordinateSpace)
    {
        return string.Equals(coordinateSpace?.Trim(), "window", StringComparison.OrdinalIgnoreCase)
            ? WindowSpaceCode
            : string.IsNullOrWhiteSpace(coordinateSpace)
                ? WindowSpaceCode
                : coordinateSpace.Trim();
    }

    private static string DecodeSpace(string? code)
    {
        return string.Equals(code?.Trim(), WindowSpaceCode, StringComparison.OrdinalIgnoreCase)
            ? "window"
            : string.IsNullOrWhiteSpace(code)
                ? "window"
                : code.Trim();
    }

    private static string EncodeUnit(string? coordinateUnit)
    {
        var normalized = coordinateUnit?.Trim();
        return normalized?.ToLowerInvariant() switch
        {
            "pixels" or "pixel" or "px" => PixelUnitCode,
            "points" or "point" or "pt" => PointUnitCode,
            "normalized" or "unit" or "ratio" or "n" => NormalizedUnitCode,
            _ => string.IsNullOrWhiteSpace(normalized) ? PixelUnitCode : normalized!
        };
    }

    private static string DecodeUnit(string? code)
    {
        return code?.Trim().ToLowerInvariant() switch
        {
            PixelUnitCode => "pixels",
            PointUnitCode => "points",
            NormalizedUnitCode => "normalized",
            null or "" => "pixels",
            _ => code!.Trim()
        };
    }

    private static int EncodeAction(string? action)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "down" or "pressed" => ActionDown,
            "move" or "moved" => ActionMove,
            "up" or "released" => ActionUp,
            "cancel" or "cancelled" or "canceled" => ActionCancel,
            "hoverenter" => ActionHoverEnter,
            "hovermove" => ActionHoverMove,
            "hoverexit" => ActionHoverExit,
            _ => ActionUnknown
        };
    }

    private static string DecodeAction(int action)
    {
        return action switch
        {
            ActionDown => "down",
            ActionMove => "move",
            ActionUp => "up",
            ActionCancel => "cancel",
            ActionHoverEnter => "hoverEnter",
            ActionHoverMove => "hoverMove",
            ActionHoverExit => "hoverExit",
            _ => "unknown"
        };
    }

    private static double? ReadSurfaceValue(IReadOnlyList<double?> surface, int index)
    {
        return index >= 0 && index < surface.Count && surface[index].GetValueOrDefault() > 0d
            ? surface[index]
            : null;
    }

    private static SessionTouchSampleDetails? ReadDetails(IReadOnlyList<object?> row)
    {
        if (row.Count < 8)
        {
            return null;
        }

        return row[7] switch
        {
            SessionTouchSampleDetails details => details,
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                element.Deserialize<SessionTouchSampleDetails>(JsonUtil.Compact),
            _ => null
        };
    }

    private static double? ResolveNormalizedCoordinate(double value, double? surfaceLength, string coordinateUnit)
    {
        if (string.Equals(coordinateUnit, "normalized", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(value, 0d, 1d);
        }

        var resolvedSurfaceLength = surfaceLength.GetValueOrDefault();
        return resolvedSurfaceLength > 0d
            ? Math.Clamp(value / resolvedSurfaceLength, 0d, 1d)
            : null;
    }

    private static bool TryReadInt(IReadOnlyList<object?> values, int index, out int value)
    {
        value = 0;
        if (!TryReadLong(values, index, out var longValue))
        {
            return false;
        }

        value = longValue > int.MaxValue
            ? int.MaxValue
            : longValue < int.MinValue
                ? int.MinValue
                : (int)longValue;
        return true;
    }

    private static bool TryReadLong(IReadOnlyList<object?> values, int index, out long value)
    {
        value = 0;
        if (index < 0 || index >= values.Count)
        {
            return false;
        }

        return TryReadLong(values[index], out value);
    }

    private static bool TryReadDouble(IReadOnlyList<object?> values, int index, out double value)
    {
        value = 0d;
        if (index < 0 || index >= values.Count)
        {
            return false;
        }

        return TryReadDouble(values[index], out value);
    }

    private static bool TryReadLong(object? rawValue, out long value)
    {
        value = 0;
        switch (rawValue)
        {
            case null:
                return false;
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue):
                value = (long)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var elementLong):
                value = elementLong;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var elementDouble):
                value = (long)Math.Round(elementDouble, MidpointRounding.AwayFromZero);
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            case string stringValue:
                return long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    private static bool TryReadDouble(object? rawValue, out double value)
    {
        value = 0d;
        switch (rawValue)
        {
            case null:
                return false;
            case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue):
                value = doubleValue;
                return true;
            case float floatValue when !float.IsNaN(floatValue) && !float.IsInfinity(floatValue):
                value = floatValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var elementDouble):
                value = elementDouble;
                return !double.IsNaN(value) && !double.IsInfinity(value);
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                       && !double.IsNaN(value)
                       && !double.IsInfinity(value);
            case string stringValue:
                return double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                       && !double.IsNaN(value)
                       && !double.IsInfinity(value);
            default:
                return false;
        }
    }

    private static string BuildTouchId(DateTimeOffset t0, int batchIndex, int rowIndex, long pointerId, long deltaMs)
    {
        return $"touch-{t0.UtcTicks:x}-{deltaMs:x}-{pointerId:x}-{batchIndex:x}-{rowIndex:x}";
    }

}
