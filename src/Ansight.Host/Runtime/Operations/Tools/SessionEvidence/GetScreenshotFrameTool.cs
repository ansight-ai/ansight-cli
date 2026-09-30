using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetScreenshotFrameTool : Operation
{
    public GetScreenshotFrameTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_screenshot_frame";

    protected override string Title => "Get Screenshot Frame";

    protected override string Description => "Return a captured screenshot frame by frame id or nearest timestamp, including image content when requested.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["frameId"] = ToolSchema.String("Optional screenshot frame id.", nullable: true),
            ["timestampUtc"] = ToolSchema.String("Optional timestamp in ISO-8601 UTC; the nearest screenshot frame is used.", nullable: true, format: "date-time"),
            ["includeImageContent"] = ToolSchema.Boolean("Include inline image content. Defaults to true.", nullable: true),
            ["includeImageBase64"] = ToolSchema.Boolean("Include imageBase64 in structured content. Defaults to false.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeImageContent", defaultValue: true, out var includeImageContent, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeImageBase64", defaultValue: false, out var includeImageBase64, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid screenshot frame arguments."));
        }

        var resolvedSnapshot = snapshot!;
        var frameId = NormalizeOptionalString(arguments?["frameId"]?.GetValue<string>());
        var frame = ResolveFrame(resolvedSnapshot, frameId, timestampUtc?.ToUniversalTime());
        if (frame is null)
        {
            return Task.FromResult(ToolError($"Session '{resolvedSnapshot.SessionId}' has no matching screenshot frame."));
        }

        var imagePath = SessionFileLocator.ResolveScreenshotPath(applicationPaths, resolvedSnapshot, frame);
        if (!File.Exists(imagePath))
        {
            return Task.FromResult(ToolError($"Screenshot frame '{frame.FrameId}' metadata exists, but the image file was not found."));
        }

        var imageBytes = File.ReadAllBytes(imagePath);
        var mimeType = SessionEvidencePayloads.ResolveImageMimeType(frame.Format);
        var framePayload = SessionEvidencePayloads.BuildScreenshotFramePayload(resolvedSnapshot, frame, imagePath, imageBytes.Length);
        var structuredContent = new JsonObject
        {
            ["session"] = SessionReviewContext.BuildSessionHeaderPayload(resolvedSnapshot, SessionReviewContext.IsLiveSession(sessionResolver, resolvedSnapshot)),
            ["targetSelector"] = frameId is not null ? "frameId" : timestampUtc.HasValue ? "timestampUtc" : "latest",
            ["frame"] = framePayload
        };

        if (includeImageBase64)
        {
            structuredContent["imageBase64"] = Convert.ToBase64String(imageBytes);
        }

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Tool call succeeded. frameId={frame.FrameId} | sessionId={resolvedSnapshot.SessionId}"
            }
        };

        if (includeImageContent)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["mimeType"] = mimeType,
                ["data"] = Convert.ToBase64String(imageBytes)
            });
        }

        return Task.FromResult(RequestResult.Success(new JsonObject
        {
            ["content"] = content,
            ["structuredContent"] = structuredContent,
            ["isError"] = false
        }));
    }

    private static SessionImageFrame? ResolveFrame(
        AppSessionSnapshot snapshot,
        string? frameId,
        DateTimeOffset? timestampUtc)
    {
        if (frameId is not null)
        {
            return snapshot.Images.FirstOrDefault(frame => string.Equals(frame.FrameId, frameId, StringComparison.Ordinal));
        }

        if (timestampUtc.HasValue)
        {
            return snapshot.Images
                .OrderBy(frame => (frame.CapturedAtUtc - timestampUtc.Value).Duration())
                .ThenBy(frame => frame.CapturedAtUtc)
                .FirstOrDefault();
        }

        return snapshot.Images
            .OrderByDescending(frame => frame.CapturedAtUtc)
            .ThenBy(frame => frame.FrameId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
