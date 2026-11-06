using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed record InspectionImage(byte[] Bytes, int Width, int Height, string Sha256,
    string? FrameId, string? WorkspacePath, DateTimeOffset? CapturedAtUtc, JsonObject? Geometry = null)
{
    public JsonObject Describe() => new()
    {
        ["frameId"] = FrameId,
        ["workspacePath"] = WorkspacePath,
        ["width"] = Width,
        ["height"] = Height,
        ["sha256"] = Sha256,
        ["capturedAtUtc"] = CapturedAtUtc,
        ["geometry"] = Geometry?.DeepClone()
    };
}
