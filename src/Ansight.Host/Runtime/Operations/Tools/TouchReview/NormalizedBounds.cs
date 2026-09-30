using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal readonly record struct NormalizedBounds(double X, double Y, double Width, double Height)
{
    public JsonObject ToPayload()
    {
        return new JsonObject
        {
            ["x"] = X,
            ["y"] = Y,
            ["width"] = Width,
            ["height"] = Height
        };
    }
}
