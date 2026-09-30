using System.Text.Json.Nodes;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class AssertLiveScreenshotTool : RemoteAppOperation
{
    public AssertLiveScreenshotTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_assert_screenshot";

    protected override string Title => "Assert Live Screenshot";

    protected override string Description => "Capture the live app and compare it with a persisted session screenshot using pixel-level difference thresholds.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["baselineFrameId"] = ToolSchema.String("Persisted screenshot frame id. Defaults to the latest frame in the session.", nullable: true),
            ["maxDifferenceRatio"] = ToolSchema.Number("Maximum changed-pixel ratio from 0 through 1. Defaults to 0.01.", nullable: true),
            ["pixelThreshold"] = ToolSchema.Integer("Per-channel difference threshold from 0 through 255. Defaults to 16.", nullable: true),
            ["maxWidth"] = ToolSchema.Integer("Capture and comparison width from 64 through 4096. Defaults to 1024.", nullable: true),
            ["actionId"] = ToolSchema.String("Optional UI action id to correlate this screenshot assertion with.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var baselineFrameId = LiveUiToolSchemas.ReadString(arguments, "baselineFrameId");
        var baseline = baselineFrameId is null
            ? snapshot!.Images.OrderBy(frame => frame.CapturedAtUtc).LastOrDefault()
            : snapshot!.Images.FirstOrDefault(frame =>
                string.Equals(frame.FrameId, baselineFrameId, StringComparison.Ordinal));
        if (baseline is null)
        {
            return ToolError(baselineFrameId is null
                ? "The session has no persisted screenshot to use as a baseline."
                : $"Screenshot frame '{baselineFrameId}' was not found in the session.");
        }

        var baselinePath = SessionFileLocator.ResolveScreenshotPath(applicationPaths, snapshot, baseline);
        if (baselinePath is null || !File.Exists(baselinePath))
        {
            return ToolError($"Baseline screenshot frame '{baseline.FrameId}' is not available on disk.");
        }

        var catalogResponse = await appToolBridge.QueryToolsAsync(
            snapshot.SessionId,
            CancellationToken.None,
            new AppToolBridgeRequestContext("host-ui", Name, correlationId));
        if (!catalogResponse.Success
            || catalogResponse.Envelope?.Payload is not { } catalog
            || !RemoteAppToolCatalog.HasTool(catalog, RemoteAppToolIds.UiGetScreenshot))
        {
            return ToolError("The live app does not expose ui.get_screenshot.");
        }

        var maxWidth = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "maxWidth", 1_024), 64, 4_096);
        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            snapshot.SessionId,
            RemoteAppToolIds.UiGetScreenshot,
            new JsonObject
            {
                ["format"] = "png",
                ["maxWidth"] = maxWidth,
                ["afterScreenUpdates"] = false
            },
            after: null,
            CancellationToken.None,
            new AppToolBridgeRequestContext("host-ui", Name, correlationId));
        if (!response.Success
            || response.Envelope?.Payload is not JsonObject payload
            || payload["result"] is not JsonObject result)
        {
            return ToolError(response.Message);
        }

        var currentPath = LiveUiNodeQuery.ReadString(result, "artifactPath");
        if (currentPath is null || !File.Exists(currentPath))
        {
            return ToolError("The live screenshot transfer did not produce a host artifact.");
        }

        var pixelThreshold = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "pixelThreshold", 16), 0, 255);
        var maxDifferenceRatio = Math.Clamp(
            LiveUiToolSchemas.ReadDouble(arguments, "maxDifferenceRatio", 0.01),
            0,
            1);
        ScreenshotDifference difference;
        try
        {
            difference = ScreenshotDifferenceCalculator.Compare(baselinePath, currentPath, maxWidth, pixelThreshold);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolError($"Screenshot comparison failed: {exception.Message}");
        }

        var passed = difference.DifferenceRatio <= maxDifferenceRatio;
        var actionId = LiveUiToolSchemas.ReadString(arguments, "actionId");
        var message = passed
            ? "Screenshot assertion passed."
            : $"Screenshot difference ratio {difference.DifferenceRatio:0.######} exceeded {maxDifferenceRatio:0.######}.";
        if (actionId is not null)
        {
            runtimeState.AddSessionLog(
                snapshot.SessionId,
                new LogEntry(DateTimeOffset.UtcNow, message)
                {
                    Source = "Ansight Screenshot Assertion",
                    Tag = "screenshot.assert",
                    EventId = actionId,
                    Priority = passed ? LogPriority.Information : LogPriority.Error
                });
        }

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "screenshot.assert",
                ["actionId"] = actionId,
                ["passed"] = passed,
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["baselineFrameId"] = baseline.FrameId,
                ["baselineCapturedAtUtc"] = baseline.CapturedAtUtc,
                ["baselinePath"] = baselinePath,
                ["currentPath"] = currentPath,
                ["width"] = difference.Width,
                ["height"] = difference.Height,
                ["differentPixelCount"] = difference.DifferentPixelCount,
                ["totalPixelCount"] = difference.TotalPixelCount,
                ["differenceRatio"] = difference.DifferenceRatio,
                ["meanChannelDifference"] = difference.MeanChannelDifference,
                ["pixelThreshold"] = pixelThreshold,
                ["maxDifferenceRatio"] = maxDifferenceRatio,
                ["message"] = message
            },
            isError: !passed);
    }
}

internal static class ScreenshotDifferenceCalculator
{
    public static ScreenshotDifference Compare(
        string baselinePath,
        string currentPath,
        int maxWidth,
        int pixelThreshold)
    {
        using var baselineSource = SKBitmap.Decode(baselinePath)
            ?? throw new InvalidOperationException("The baseline image could not be decoded.");
        using var currentSource = SKBitmap.Decode(currentPath)
            ?? throw new InvalidOperationException("The current image could not be decoded.");
        var targetWidth = Math.Min(maxWidth, baselineSource.Width);
        var targetHeight = Math.Max(1, (int)Math.Round(
            baselineSource.Height * (targetWidth / (double)baselineSource.Width)));
        using var baseline = Resize(baselineSource, targetWidth, targetHeight);
        using var current = Resize(currentSource, targetWidth, targetHeight);
        var baselinePixels = baseline.Pixels;
        var currentPixels = current.Pixels;
        var differentPixels = 0L;
        var absoluteChannelDifference = 0L;
        for (var index = 0; index < baselinePixels.Length; index++)
        {
            var left = baselinePixels[index];
            var right = currentPixels[index];
            var red = Math.Abs(left.Red - right.Red);
            var green = Math.Abs(left.Green - right.Green);
            var blue = Math.Abs(left.Blue - right.Blue);
            var alpha = Math.Abs(left.Alpha - right.Alpha);
            absoluteChannelDifference += red + green + blue + alpha;
            if (Math.Max(Math.Max(red, green), Math.Max(blue, alpha)) > pixelThreshold)
            {
                differentPixels++;
            }
        }

        var totalPixels = baselinePixels.LongLength;
        return new ScreenshotDifference(
            targetWidth,
            targetHeight,
            differentPixels,
            totalPixels,
            totalPixels == 0 ? 0 : differentPixels / (double)totalPixels,
            totalPixels == 0 ? 0 : absoluteChannelDifference / (double)(totalPixels * 4));
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return source.Copy();
        }

        var resized = source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
        return resized ?? throw new InvalidOperationException("The screenshot could not be resized for comparison.");
    }
}

internal sealed record ScreenshotDifference(
    int Width,
    int Height,
    long DifferentPixelCount,
    long TotalPixelCount,
    double DifferenceRatio,
    double MeanChannelDifference);
