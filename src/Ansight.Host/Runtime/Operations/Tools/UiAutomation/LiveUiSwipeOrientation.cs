namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal readonly record struct LiveUiSwipeVector(double X, double Y);

internal static class LiveUiSwipeOrientation
{
    public static bool TryParse(string? value, out LiveUiSwipeVector vector)
    {
        var normalized = Normalize(value);
        if (TryParsePoint(normalized, out vector))
        {
            return true;
        }

        var separatorIndex = normalized.IndexOf("to", StringComparison.Ordinal);
        if (separatorIndex <= 0
            || separatorIndex >= normalized.Length - 2
            || !TryParsePoint(normalized[..separatorIndex], out var start)
            || !TryParsePoint(normalized[(separatorIndex + 2)..], out var end))
        {
            vector = default;
            return false;
        }

        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var magnitude = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (magnitude <= 0)
        {
            vector = default;
            return false;
        }

        vector = new LiveUiSwipeVector(deltaX / magnitude, deltaY / magnitude);
        return true;
    }

    private static bool TryParsePoint(string value, out LiveUiSwipeVector vector)
    {
        vector = value switch
        {
            "n" or "north" or "up" => new LiveUiSwipeVector(0, -1),
            "ne" or "northeast" or "upright" => Diagonal(1, -1),
            "e" or "east" or "right" => new LiveUiSwipeVector(1, 0),
            "se" or "southeast" or "downright" => Diagonal(1, 1),
            "s" or "south" or "down" => new LiveUiSwipeVector(0, 1),
            "sw" or "southwest" or "downleft" => Diagonal(-1, 1),
            "w" or "west" or "left" => new LiveUiSwipeVector(-1, 0),
            "nw" or "northwest" or "upleft" => Diagonal(-1, -1),
            _ => default
        };
        return vector != default;
    }

    private static LiveUiSwipeVector Diagonal(double x, double y)
    {
        var component = 1 / Math.Sqrt(2);
        return new LiveUiSwipeVector(x * component, y * component);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "up"
            : value.Trim()
                .ToLowerInvariant()
                .Replace("→", "to", StringComparison.Ordinal)
                .Replace("->", "to", StringComparison.Ordinal)
                .Replace("–", "to", StringComparison.Ordinal)
                .Replace("—", "to", StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);
    }
}
