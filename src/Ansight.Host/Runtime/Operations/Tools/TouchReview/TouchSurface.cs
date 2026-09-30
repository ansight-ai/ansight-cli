namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal readonly record struct TouchSurface(double X, double Y, double? Width, double? Height)
{
    public static TouchSurface Empty { get; } = new(0, 0, null, null);
}
