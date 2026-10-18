using System.Text.Json.Nodes;
using Ansight.Tools;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class AssertLiveScreenshotTool : RemoteAppOperation
{
    private readonly TakeScreenshotTool captureScreenshot;
    public AssertLiveScreenshotTool(OperationServices services)
        : base(services)
    {
        captureScreenshot = new TakeScreenshotTool(services, flatResult: true);
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
            ["ignoreRegions"] = ScreenshotIgnoreRegions.Schema(),
            ["ignoreSystemUi"] = ScreenshotSystemUiMasks.Schema(),
            ["includeSystemUiMetadata"] = ToolSchema.Boolean("Include system-UI metadata with the actual capture. Defaults to true.", nullable: true),
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

        IReadOnlyList<ScreenshotIgnoreRegion> ignoreRegions;
        IReadOnlyList<string> kinds;
        bool includeSystemUiMetadata;
        try
        {
            ignoreRegions = ScreenshotIgnoreRegions.Read(arguments?["ignoreRegions"]);
            kinds = ScreenshotSystemUiMasks.Read(arguments?["ignoreSystemUi"]);
            if (arguments?["includeSystemUiMetadata"] is { } option
                && (option is not JsonValue value || !value.TryGetValue<bool>(out _)))
                throw new ArgumentException("includeSystemUiMetadata must be a boolean.");
            includeSystemUiMetadata = arguments?["includeSystemUiMetadata"]?.GetValue<bool>() ?? true;
            if (!includeSystemUiMetadata && kinds.Count > 0)
                throw new ArgumentException("ignoreSystemUi requires includeSystemUiMetadata: true on this assertion.");
        }
        catch (ArgumentException exception) { return ToolError(exception.Message); }
        var maxWidth = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "maxWidth", 1_024), 64, 4_096);
        var captured = await captureScreenshot.ExecuteAsync(new JsonObject
        {
            ["sessionId"] = snapshot.SessionId, ["format"] = "png", ["maxWidth"] = maxWidth,
            ["afterScreenUpdates"] = false, ["includeSystemUiMetadata"] = includeSystemUiMetadata
        }, correlationId).ConfigureAwait(false);
        if (captured.IsError || captured.Payload?["isError"]?.GetValue<bool>() == true) return captured;
        var current = captured.Payload!["structuredContent"]!;
        var currentPath = current["artifactPath"]!.GetValue<string>();

        var pixelThreshold = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "pixelThreshold", 16), 0, 255);
        var maxDifferenceRatio = Math.Clamp(
            LiveUiToolSchemas.ReadDouble(arguments, "maxDifferenceRatio", 0.01),
            0,
            1);
        ScreenshotDifference difference;
        ScreenshotSystemUiMaskResolution masks;
        try
        {
            masks = ScreenshotSystemUiMasks.Resolve(current["geometry"], kinds, ignoreRegions, current["frameId"]?.GetValue<string>());
            ignoreRegions = masks.Regions;
            difference = ScreenshotDifferenceCalculator.Compare(baselinePath, currentPath, maxWidth, pixelThreshold,
                ignoreRegions, ToolExecutionCancellation.Current);
        }
        catch (ScreenshotGeometryUnavailableException exception)
        {
            return RequestResult.ToolResult(new JsonObject { ["errorCode"] = "screenshot_geometry_unavailable", ["message"] = exception.Message }, isError: true);
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
                ["currentFrameId"] = current["frameId"]!.DeepClone(),
                ["width"] = difference.Width,
                ["height"] = difference.Height,
                ["differentPixelCount"] = difference.DifferentPixelCount,
                ["totalPixelCount"] = difference.TotalPixelCount,
                ["ignoredPixelCount"] = difference.IgnoredPixelCount,
                ["ignoreRegions"] = ScreenshotIgnoreRegions.Describe(ignoreRegions),
                ["ignoreSystemUi"] = new JsonArray(kinds.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray()),
                ["systemUiMaskResolutions"] = masks.Resolutions,
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
        int pixelThreshold,
        IReadOnlyList<ScreenshotIgnoreRegion>? ignoreRegions = null,
        CancellationToken token = default)
    {
        using var baselineSource = SKBitmap.Decode(baselinePath)
            ?? throw new InvalidOperationException("The baseline image could not be decoded.");
        using var currentSource = SKBitmap.Decode(currentPath)
            ?? throw new InvalidOperationException("The current image could not be decoded.");
        var targetWidth = Math.Min(maxWidth, baselineSource.Width);
        var targetHeight = Math.Max(1, (int)Math.Round(
            baselineSource.Height * (targetWidth / (double)baselineSource.Width)));
        var mask = ScreenshotIgnoreRegions.Rasterize(ignoreRegions ?? [], targetWidth, targetHeight, token);
        using var baseline = Resize(baselineSource, targetWidth, targetHeight);
        using var current = Resize(currentSource, targetWidth, targetHeight);
        var baselinePixels = baseline.Pixels;
        var currentPixels = current.Pixels;
        var differentPixels = 0L;
        var absoluteChannelDifference = 0L;
        for (var index = 0; index < baselinePixels.Length; index++)
        {
            if (index % targetWidth == 0) token.ThrowIfCancellationRequested();
            if (mask.Pixels.Length > 0 && mask.Pixels[index]) continue;
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

        var totalPixels = baselinePixels.LongLength - mask.IgnoredPixelCount;
        return new ScreenshotDifference(
            targetWidth,
            targetHeight,
            differentPixels,
            totalPixels,
            totalPixels == 0 ? 0 : differentPixels / (double)totalPixels,
            totalPixels == 0 ? 0 : absoluteChannelDifference / (double)(totalPixels * 4),
            mask.IgnoredPixelCount);
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
    double MeanChannelDifference,
    long IgnoredPixelCount);
