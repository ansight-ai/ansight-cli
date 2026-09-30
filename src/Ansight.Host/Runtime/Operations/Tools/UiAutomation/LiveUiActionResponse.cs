using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiActionResponse
{
    public static RequestResult Build(
        JsonObject structuredContent,
        ActionEvidenceCapture? finalEvidence,
        bool includeScreenshot,
        bool isError)
    {
        if (!includeScreenshot
            || string.IsNullOrWhiteSpace(finalEvidence?.ScreenshotArtifactPath)
            || !File.Exists(finalEvidence.ScreenshotArtifactPath))
        {
            return RequestResult.ToolResult(structuredContent, isError);
        }

        byte[] imageBytes;
        try
        {
            imageBytes = File.ReadAllBytes(finalEvidence.ScreenshotArtifactPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RequestResult.ToolResult(structuredContent, isError);
        }

        return RequestResult.Success(new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ToolResultSummary.Build(structuredContent, isError)
                },
                new JsonObject
                {
                    ["type"] = "image",
                    ["mimeType"] = SessionEvidencePayloads.ResolveImageMimeType(finalEvidence.ScreenshotFormat),
                    ["data"] = Convert.ToBase64String(imageBytes)
                }
            },
            ["structuredContent"] = structuredContent,
            ["isError"] = isError
        });
    }
}
