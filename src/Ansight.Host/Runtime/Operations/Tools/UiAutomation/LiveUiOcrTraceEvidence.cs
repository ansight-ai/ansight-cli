using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiOcrTraceEvidence
{
    public const string PayloadPropertyName = "_ansightOcrTraceEvidence";

    public static JsonObject Create(
        PersistedScreenshotEvidence screenshot,
        SessionScreenshotOcrResult scan,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        ArgumentNullException.ThrowIfNull(scan);

        return new JsonObject
        {
            ["kind"] = "ocr",
            ["provider"] = scan.Provider,
            ["available"] = scan.Available,
            ["message"] = scan.Message,
            ["capturedAtUtc"] = screenshot.Frame.CapturedAtUtc,
            ["screenshotFrameId"] = screenshot.Frame.FrameId,
            ["screenshotSha256"] = screenshot.Hash,
            ["screenshotFormat"] = screenshot.Frame.Format,
            ["screenshotArtifactPath"] = screenshot.ArtifactPath,
            ["screenWidth"] = imageWidth,
            ["screenHeight"] = imageHeight,
            ["detectionCount"] = scan.Blocks.Count,
            ["detections"] = new JsonArray(
                scan.Blocks.Select(block => (JsonNode?)ToJson(block, imageWidth, imageHeight)).ToArray())
        };
    }

    public static JsonObject? RemoveFrom(JsonNode? structuredContent)
    {
        if (structuredContent is not JsonObject content
            || content[PayloadPropertyName] is not JsonObject evidence)
        {
            return null;
        }

        content.Remove(PayloadPropertyName);
        return evidence.DeepClone().AsObject();
    }

    private static JsonObject ToJson(
        SessionScreenshotTextBlock block,
        int imageWidth,
        int imageHeight)
        => new()
        {
            ["text"] = block.Text,
            ["confidence"] = Math.Clamp(block.Confidence / 100, 0, 1),
            ["pixelBounds"] = new JsonObject
            {
                ["x"] = block.Bounds.X,
                ["y"] = block.Bounds.Y,
                ["width"] = block.Bounds.Width,
                ["height"] = block.Bounds.Height
            },
            ["normalizedBounds"] = new JsonObject
            {
                ["x"] = Math.Clamp(block.Bounds.X / (double)imageWidth, 0, 1),
                ["y"] = Math.Clamp(block.Bounds.Y / (double)imageHeight, 0, 1),
                ["width"] = Math.Clamp(block.Bounds.Width / (double)imageWidth, 0, 1),
                ["height"] = Math.Clamp(block.Bounds.Height / (double)imageHeight, 0, 1)
            }
        };
}
