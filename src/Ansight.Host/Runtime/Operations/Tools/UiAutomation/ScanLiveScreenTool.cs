using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class ScanLiveScreenTool : RemoteAppOperation
{
    private readonly ISessionScreenshotOcrScanner ocrScanner;

    public ScanLiveScreenTool(OperationServices services)
        : this(services, new TesseractSessionScreenshotOcrScanner())
    {
    }

    internal ScanLiveScreenTool(
        OperationServices services,
        ISessionScreenshotOcrScanner ocrScanner)
        : base(services)
    {
        this.ocrScanner = ocrScanner ?? throw new ArgumentNullException(nameof(ocrScanner));
    }

    public override string Name => "ansight_scan_screen";

    protected override string Title => "Scan Visible Screen Content";

    protected override string Description =>
        "Read the current rendered screenshot as human-visible text with normalized bounds and current-layout tap hints. This complements accessibility for custom-rendered and unrepresented content; it does not prove off-screen absence or provide stable automation IDs.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["limit"] = ToolSchema.Integer("Maximum visible text lines to return. Defaults to 60.", nullable: true),
            ["minimumConfidence"] = ToolSchema.Number("Minimum OCR confidence from 0 through 1. Defaults to 0.35.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!RunRequestContext.AllowsScreenshotOcr(correlationId))
        {
            return ToolError(
                "Screenshot OCR and screen scanning are disabled for agent execution.");
        }

        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var session = snapshot!;
        var screenshot = await LiveUiActionEvidence.CaptureScreenshotAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            session,
            "scan-live-screen",
            "current",
            correlationId,
            CancellationToken.None, externalScreenshots: externalScreenshots);
        if (screenshot is null || !File.Exists(screenshot.ArtifactPath))
        {
            return ToolError("A current rendered screenshot was unavailable for screen-content scanning.");
        }

        SessionScreenshotOcrResult scan;
        try
        {
            scan = await Task.Run(
                () => ocrScanner.Scan(screenshot.ArtifactPath),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException)
        {
            return ToolError($"Screenshot OCR failed: {exception.Message}");
        }

        if (!scan.Available)
        {
            return ToolError(scan.Message ?? "Screenshot OCR was unavailable.");
        }

        using var bitmap = SKBitmap.Decode(screenshot.ArtifactPath);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return ToolError("The current screenshot could not be decoded for OCR coordinates.");
        }

        var limit = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "limit", 60), 1, 200);
        var minimumConfidence = Math.Clamp(
            LiveUiToolSchemas.ReadDouble(arguments, "minimumConfidence", 0.35),
            0,
            1);
        var eligibleBlocks = scan.Blocks
            .Where(block => block.Confidence / 100 >= minimumConfidence)
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToArray();
        var textCounts = eligibleBlocks
            .GroupBy(block => block.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var visibleContent = new JsonArray(
            eligibleBlocks
                .Take(limit)
                .Select(block => (JsonNode?)ToJson(
                    block,
                    bitmap.Width,
                    bitmap.Height,
                    textCounts[block.Text.Trim()] == 1))
                .ToArray());

        var traceEvidence = LiveUiOcrTraceEvidence.Create(
            screenshot,
            scan,
            bitmap.Width,
            bitmap.Height);

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "ui.scan_screen",
                ["sessionId"] = session.SessionId,
                ["appId"] = session.AppId,
                ["evidenceSource"] = "screenshotOcr",
                ["provider"] = scan.Provider,
                ["capturedAtUtc"] = screenshot.Frame.CapturedAtUtc,
                ["screenshotFrameId"] = screenshot.Frame.FrameId,
                ["screenWidth"] = bitmap.Width,
                ["screenHeight"] = bitmap.Height,
                ["content"] = visibleContent,
                ["count"] = visibleContent.Count,
                ["totalCount"] = eligibleBlocks.Length,
                ["truncated"] = eligibleBlocks.Length > visibleContent.Count,
                ["warning"] = "Screen text is current-pixel evidence only. Tap hints expire after any layout change and do not substitute for stable automation IDs in reusable graph transitions.",
                [LiveUiOcrTraceEvidence.PayloadPropertyName] = traceEvidence
            },
            isError: false);
    }

    private static JsonObject ToJson(
        SessionScreenshotTextBlock block,
        int imageWidth,
        int imageHeight,
        bool includeTapHint)
    {
        var normalizedX = Math.Clamp(
            (block.Bounds.X + (block.Bounds.Width / 2)) / imageWidth,
            0,
            1);
        var normalizedY = Math.Clamp(
            (block.Bounds.Y + (block.Bounds.Height / 2)) / imageHeight,
            0,
            1);
        var result = new JsonObject
        {
            ["text"] = block.Text,
            ["confidence"] = Math.Clamp(block.Confidence / 100, 0, 1),
            ["bounds"] = new JsonObject
            {
                ["x"] = Math.Clamp(block.Bounds.X / imageWidth, 0, 1),
                ["y"] = Math.Clamp(block.Bounds.Y / imageHeight, 0, 1),
                ["width"] = Math.Clamp(block.Bounds.Width / imageWidth, 0, 1),
                ["height"] = Math.Clamp(block.Bounds.Height / imageHeight, 0, 1)
            }
        };
        if (includeTapHint)
        {
            result["tapHint"] = new JsonObject
            {
                ["tool"] = "ansight_tap_ui",
                ["selector"] = new JsonObject
                {
                    ["normalizedX"] = normalizedX,
                    ["normalizedY"] = normalizedY
                }
            };
        }

        return result;
    }
}
