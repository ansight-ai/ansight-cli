using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiOcrSearch
{
    public static async Task<LiveUiOcrSearchResult> FindAsync(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IAppToolBridge appToolBridge,
        ISessionScreenshotOcrScanner ocrScanner,
        AppSessionSnapshot session,
        LiveUiBounds? viewport,
        LiveUiSelector selector,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null)
    {
        if (!selector.CanUseOcr)
        {
            return LiveUiOcrSearchResult.NotAttempted;
        }

        var screenshot = await LiveUiActionEvidence.CaptureScreenshotAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            session,
            operationName,
            "current",
            correlationId,
            cancellationToken, externalScreenshots: externalScreenshots);
        if (screenshot is null || !File.Exists(screenshot.ArtifactPath))
        {
            return LiveUiOcrSearchResult.Unavailable(
                "A current screenshot was unavailable for OCR.");
        }

        SessionScreenshotOcrResult scan;
        try
        {
            scan = await Task.Run(
                () => ocrScanner.Scan(screenshot.ArtifactPath),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException)
        {
            scan = SessionScreenshotOcrResult.Unavailable(
                $"Screenshot OCR failed: {exception.Message}");
        }

        using var bitmap = SKBitmap.Decode(screenshot.ArtifactPath);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return LiveUiOcrSearchResult.Unavailable(
                "The current screenshot could not be decoded for OCR coordinates.");
        }

        var traceEvidence = LiveUiOcrTraceEvidence.Create(
            screenshot,
            scan,
            bitmap.Width,
            bitmap.Height);
        if (!scan.Available)
        {
            return LiveUiOcrSearchResult.Unavailable(
                scan.Message ?? "Screenshot OCR was unavailable.",
                traceEvidence);
        }

        var blockMatches = scan.Blocks
            .Select(block => new LiveUiOcrTextMatch(
                block,
                selector.EvaluateOcrText(block.Text)))
            .Where(static match => match.Evaluation.IsMatch)
            .OrderByDescending(static match => match.Evaluation.Score)
            .ThenByDescending(static match => match.Block.Confidence)
            .ToArray();
        var matches = blockMatches
            .Select(match => ToResultJson(
                match.Block,
                selector.Text!,
                bitmap.Width,
                bitmap.Height,
                viewport,
                includeTapHint: selector.MatchMode != LiveUiStringMatchMode.Fuzzy
                                && (blockMatches.Length == 1 || selector.IndexSpecified),
                evaluation: match.Evaluation,
                matchMode: selector.MatchMode))
            .ToArray();
        return new LiveUiOcrSearchResult(
            true,
            scan.Provider,
            scan.Message,
            matches,
            traceEvidence);
    }

    private static JsonObject ToResultJson(
        SessionScreenshotTextBlock block,
        string matchedText,
        int imageWidth,
        int imageHeight,
        LiveUiBounds? viewport,
        bool includeTapHint,
        LiveUiSelectorMatchResult evaluation,
        LiveUiStringMatchMode matchMode)
    {
        var normalizedX = Math.Clamp(
            (block.Bounds.X + (block.Bounds.Width / 2)) / imageWidth,
            0,
            1);
        var normalizedY = Math.Clamp(
            (block.Bounds.Y + (block.Bounds.Height / 2)) / imageHeight,
            0,
            1);
        var bounds = viewport is null
            ? new LiveUiBounds(
                block.Bounds.X,
                block.Bounds.Y,
                block.Bounds.Width,
                block.Bounds.Height)
            : new LiveUiBounds(
                viewport.X + ((block.Bounds.X / imageWidth) * viewport.Width),
                viewport.Y + ((block.Bounds.Y / imageHeight) * viewport.Height),
                (block.Bounds.Width / imageWidth) * viewport.Width,
                (block.Bounds.Height / imageHeight) * viewport.Height);
        var result = new JsonObject
        {
            ["source"] = "ocr",
            ["text"] = block.Text,
            ["matchedText"] = matchedText,
            ["role"] = "text",
            ["supportedActions"] = new JsonArray(),
            ["visible"] = true,
            ["enabled"] = true,
            ["onScreen"] = true,
            ["viewportRelation"] = "inside",
            ["confidence"] = Math.Clamp(block.Confidence / 100, 0, 1),
            ["bounds"] = bounds.ToJson()
        };
        if (matchMode == LiveUiStringMatchMode.Fuzzy)
        {
            result["matchMode"] = "fuzzy";
            result["matchScore"] = evaluation.Score;
            result["matchReason"] = evaluation.Reason;
        }
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

internal sealed record LiveUiOcrTextMatch(
    SessionScreenshotTextBlock Block,
    LiveUiSelectorMatchResult Evaluation);

internal sealed record LiveUiOcrSearchResult(
    bool Available,
    string? Provider,
    string? Message,
    IReadOnlyList<JsonObject> Matches,
    JsonObject? TraceEvidence)
{
    public static LiveUiOcrSearchResult NotAttempted { get; } = new(false, null, null, [], null);

    public bool Attempted => !ReferenceEquals(this, NotAttempted);

    public static LiveUiOcrSearchResult Unavailable(string message, JsonObject? traceEvidence = null)
        => new(false, null, message, [], traceEvidence);

    public bool TryGetUniqueTapPoint(out LiveUiScreenPoint point, out JsonObject? match)
        => TryGetTapPoint(index: 0, indexSpecified: false, out point, out match);

    public bool TryGetTapPoint(
        int index,
        bool indexSpecified,
        out LiveUiScreenPoint point,
        out JsonObject? match)
    {
        match = indexSpecified
            ? Matches.ElementAtOrDefault(index)
            : Matches.Count == 1 ? Matches[0] : null;
        if (match?["tapHint"]?["selector"] is not JsonObject selector
            || selector["normalizedX"] is not JsonValue normalizedXValue
            || !normalizedXValue.TryGetValue<double>(out var normalizedX)
            || selector["normalizedY"] is not JsonValue normalizedYValue
            || !normalizedYValue.TryGetValue<double>(out var normalizedY))
        {
            point = default;
            return false;
        }

        point = new LiveUiScreenPoint(normalizedX, normalizedY);
        return true;
    }

    public JsonObject ToJson()
        => new()
        {
            ["attempted"] = Attempted,
            ["available"] = Available,
            ["provider"] = Provider,
            ["message"] = Message,
            ["matchCount"] = Matches.Count
        };
}
