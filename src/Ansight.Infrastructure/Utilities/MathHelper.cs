using System;
namespace Ansight.Infrastructure.Utilities;

public static class MathHelper
{
    public static bool ApproximatelyEquals(double left, double right, double tolerance = 0.00000001)
    {
        var diff = Math.Abs(left - right);
        return diff <= tolerance ||
               diff <= Math.Max(Math.Abs(left), Math.Abs(right)) * tolerance;
    }
}