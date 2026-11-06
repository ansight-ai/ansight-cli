using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record LiveUiBounds(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public JsonObject ToJson()
        => new()
        {
            ["x"] = X,
            ["y"] = Y,
            ["width"] = Width,
            ["height"] = Height
        };

    public static LiveUiBounds? Union(IReadOnlyList<LiveUiBounds> bounds)
    {
        if (bounds.Count == 0)
        {
            return null;
        }

        var left = bounds.Min(candidate => candidate.X);
        var top = bounds.Min(candidate => candidate.Y);
        var right = bounds.Max(candidate => candidate.X + candidate.Width);
        var bottom = bounds.Max(candidate => candidate.Y + candidate.Height);

        return new LiveUiBounds(left, top, right - left, bottom - top);
    }

    public static LiveUiBounds? Intersection(LiveUiBounds left, LiveUiBounds right)
    {
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);

        return intersectionRight > intersectionLeft && intersectionBottom > intersectionTop
            ? new LiveUiBounds(
                intersectionLeft,
                intersectionTop,
                intersectionRight - intersectionLeft,
                intersectionBottom - intersectionTop)
            : null;
    }
}
