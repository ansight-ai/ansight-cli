using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class WaitForLiveUiTool : RemoteAppOperation
{
    private const int MinimumNativeVisualTreeIntervalMilliseconds = 1_000;
    private readonly ISessionScreenshotOcrScanner ocrScanner;

    public WaitForLiveUiTool(OperationServices services)
        : this(services, new TesseractSessionScreenshotOcrScanner())
    {
    }

    internal WaitForLiveUiTool(
        OperationServices services,
        ISessionScreenshotOcrScanner ocrScanner)
        : base(services)
    {
        this.ocrScanner = ocrScanner ?? throw new ArgumentNullException(nameof(ocrScanner));
    }

    public override string Name => "ansight_wait_for_ui";

    protected override string Title => "Wait For Live UI";

    protected override string Description => "Wait for UI using fresh device accessibility samples first, then framework semantics, screenshot OCR, and throttled native visual-tree fallback. Captured trees are retained as timeline evidence.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: LiveUiToolSchemas.SelectorProperties(
            new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
                ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
                ["condition"] = ToolSchema.String(
                    "Condition to wait for. Stable may target a selected subtree or the whole tree.",
                    enumValues: ["visible", "hidden", "stable"],
                    nullable: true),
                ["timeoutMs"] = ToolSchema.Integer("Timeout from 100 through 60000 milliseconds. Defaults to 10000.", nullable: true),
                ["pollIntervalMs"] = ToolSchema.Integer("Polling interval from 100 through 5000 milliseconds. Defaults to 250.", nullable: true),
                ["stableSamples"] = ToolSchema.Integer("Equal consecutive tree samples required for stable. Defaults to 3.", nullable: true)
            }),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveWaitableSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }
        var session = snapshot!;

        var condition = LiveUiToolSchemas.ReadString(arguments, "condition")?.ToLowerInvariant() ?? "visible";
        if (condition is not ("visible" or "hidden" or "stable"))
        {
            return ToolError("condition must be visible, hidden, or stable.");
        }

        var selector = LiveUiSelector.Parse(arguments);
        if (condition != "stable" && !selector.HasCriteria)
        {
            return ToolError("A stable UI selector is required for visible or hidden waits.");
        }

        var timeoutMs = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "timeoutMs", 10_000), 100, 60_000);
        var pollIntervalMs = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "pollIntervalMs", 250), 100, 5_000);
        var requiredStableSamples = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "stableSamples", 3), 2, 20);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var stableSamples = 0;
        string? previousHash = null;
        string? finalHash = null;
        string? latestError = null;
        LiveUiTreeCapture? latestCapture = null;
        IReadOnlyList<LiveUiNodeMatch> latestMatches = [];
        var latestOcrResult = LiveUiOcrSearchResult.NotAttempted;
        long nextNativeVisualTreeAtMilliseconds = 0;
        var stableUsesNativeTree = false;

        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            attempts++;
            var capturedStableTarget = false;
            var captureResult = condition == "stable" && stableUsesNativeTree
                ? await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(
                    session,
                    appToolBridge,
                    Name,
                    correlationId,
                    ToolExecutionCancellation.Current)
                : await LiveUiTreeCapture.CaptureForSelectorAsync(
                    session,
                    appToolBridge,
                    uiInputRouter,
                    Name,
                    correlationId,
                    selector,
                    ToolExecutionCancellation.Current,
                    allowCached: false);
            var primaryEvidenceSource = stableUsesNativeTree ? "visualTree" : "accessibility";
            if (captureResult.IsSuccess && captureResult.Capture is { } capture)
            {
                latestError = null;
                latestCapture = capture;
                PersistTimelineTree(capture);
                latestMatches = selector.HasCriteria
                    ? OrderMatches(capture, selector)
                    : [];
                var visibleMatches = SelectVisibleMatches(latestMatches, selector);
                if (condition == "visible" && visibleMatches.Count > 0)
                {
                    return BuildSuccess(
                        condition,
                        selector,
                        capture,
                        visibleMatches,
                        "accessibility",
                        attempts,
                        stopwatch.ElapsedMilliseconds,
                        treeHash: null);
                }

                if (condition == "stable")
                {
                    capturedStableTarget = !selector.HasCriteria
                                           || selector.IsAvailable(latestMatches.Count);
                    if (ObserveStableSample(
                            capture,
                            selector,
                            latestMatches,
                            requiredStableSamples,
                            ref stableSamples,
                            ref previousHash,
                            ref finalHash))
                    {
                        return BuildSuccess(
                            condition,
                            selector,
                            capture,
                            latestMatches,
                            primaryEvidenceSource,
                            attempts,
                            stopwatch.ElapsedMilliseconds,
                            finalHash);
                    }
                }
            }
            else
            {
                latestError = captureResult.Message;
                if (condition != "stable" || stableUsesNativeTree)
                {
                    stableSamples = 0;
                    previousHash = null;
                }
            }

            if (condition is "visible" or "hidden")
            {
                if (RunRequestContext.AllowsScreenshotOcr(correlationId))
                {
                    latestOcrResult = await LiveUiOcrSearch.FindAsync(
                        runtimeState,
                        applicationPaths,
                        appToolBridge,
                        ocrScanner,
                        session,
                        latestCapture?.Viewport,
                        selector,
                        "wait-ui-ocr",
                        correlationId,
                        ToolExecutionCancellation.Current, externalScreenshots: externalScreenshots);
                }
                var selectedOcrMatches = selector.ApplyIndex(latestOcrResult.Matches);
                if (selectedOcrMatches.Count > 0)
                {
                    if (condition == "visible")
                    {
                        return BuildOcrSuccess(
                            condition,
                            selector,
                            session,
                            latestOcrResult,
                            attempts,
                            stopwatch.ElapsedMilliseconds);
                    }
                }
                else if (stopwatch.ElapsedMilliseconds >= nextNativeVisualTreeAtMilliseconds)
                {
                    nextNativeVisualTreeAtMilliseconds = stopwatch.ElapsedMilliseconds
                                                         + Math.Max(
                                                             MinimumNativeVisualTreeIntervalMilliseconds,
                                                             pollIntervalMs * 4L);
                    var nativeResult = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(
                        session,
                        appToolBridge,
                        Name,
                        correlationId,
                        ToolExecutionCancellation.Current);
                    if (nativeResult.Capture is { } nativeCapture)
                    {
                        latestError = null;
                        latestCapture = nativeCapture;
                        PersistTimelineTree(nativeCapture);
                        latestMatches = OrderMatches(nativeCapture, selector);
                        var visibleNativeMatches = SelectVisibleMatches(latestMatches, selector);
                        if (condition == "visible" && visibleNativeMatches.Count > 0)
                        {
                            return BuildSuccess(
                                condition,
                                selector,
                                nativeCapture,
                                visibleNativeMatches,
                                "visualTree",
                                attempts,
                                stopwatch.ElapsedMilliseconds,
                                treeHash: null);
                        }

                        if (condition == "hidden" && visibleNativeMatches.Count == 0)
                        {
                            return BuildSuccess(
                                condition,
                                selector,
                                nativeCapture,
                                visibleNativeMatches,
                                "visualTree",
                                attempts,
                                stopwatch.ElapsedMilliseconds,
                                treeHash: null);
                        }
                    }
                    else if (condition == "hidden" && captureResult.IsSuccess)
                    {
                        return BuildSuccess(
                            condition,
                            selector,
                            captureResult.Capture!,
                            [],
                            "accessibility",
                            attempts,
                            stopwatch.ElapsedMilliseconds,
                            treeHash: null);
                    }
                    else
                    {
                        latestError = nativeResult.Message;
                    }
                }
            }
            else if (condition == "stable"
                     && !capturedStableTarget
                     && stopwatch.ElapsedMilliseconds >= nextNativeVisualTreeAtMilliseconds)
            {
                nextNativeVisualTreeAtMilliseconds = stopwatch.ElapsedMilliseconds
                                                     + Math.Max(
                                                         MinimumNativeVisualTreeIntervalMilliseconds,
                                                         pollIntervalMs * 4L);
                var nativeResult = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(
                    session,
                    appToolBridge,
                    Name,
                    correlationId,
                    ToolExecutionCancellation.Current);
                if (nativeResult.Capture is { } nativeCapture)
                {
                    stableUsesNativeTree = true;
                    latestError = null;
                    latestCapture = nativeCapture;
                    PersistTimelineTree(nativeCapture);
                    latestMatches = selector.HasCriteria
                        ? OrderMatches(nativeCapture, selector)
                        : [];
                    if (ObserveStableSample(
                            nativeCapture,
                            selector,
                            latestMatches,
                            requiredStableSamples,
                            ref stableSamples,
                            ref previousHash,
                            ref finalHash))
                    {
                        return BuildSuccess(
                            condition,
                            selector,
                            nativeCapture,
                            latestMatches,
                            "visualTree",
                            attempts,
                            stopwatch.ElapsedMilliseconds,
                            finalHash);
                    }
                }
                else
                {
                    latestError = nativeResult.Message;
                    stableSamples = 0;
                    previousHash = null;
                }
            }

            var remainingMs = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            await Task.Delay(
                (int)Math.Min(pollIntervalMs, remainingMs),
                ToolExecutionCancellation.Current);
        }

        var reportedMatches = condition is "visible" or "hidden"
            ? SelectVisibleMatches(latestMatches, selector)
            : latestMatches;

        var payload = new JsonObject
        {
            ["capability"] = "ui.wait_for",
            ["condition"] = condition,
            ["satisfied"] = false,
            ["sessionId"] = session.SessionId,
            ["appId"] = session.AppId,
            ["selector"] = selector.ToJson(),
            ["attempts"] = attempts,
            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
            ["timeoutMs"] = timeoutMs,
            ["matchCount"] = reportedMatches.Count,
            ["stableSamples"] = stableSamples,
            ["treeHash"] = finalHash,
            ["message"] = latestError ?? $"Timed out waiting for UI condition '{condition}'."
        };
        if (latestCapture is not null)
        {
            payload["capturedAtUtc"] = latestCapture.CapturedAtUtc;
            payload["matches"] = new JsonArray(reportedMatches.Take(20).Select(LiveUiNodeQuery.ToResultJson).ToArray());
        }
        payload["ocr"] = latestOcrResult.ToJson();

        return RequestResult.ToolResult(payload, isError: true);
    }

    internal static IReadOnlyList<LiveUiNodeMatch> FilterVisibleMatches(
        IReadOnlyList<LiveUiNodeMatch> matches)
        => matches.Where(LiveUiNodeQuery.IsEffectivelyVisible).ToArray();

    internal static IReadOnlyList<LiveUiNodeMatch> SelectVisibleMatches(
        IReadOnlyList<LiveUiNodeMatch> matches,
        LiveUiSelector selector)
    {
        return FilterVisibleMatches(selector.ApplyIndex(matches));
    }

    private static IReadOnlyList<LiveUiNodeMatch> OrderMatches(
        LiveUiTreeCapture capture,
        LiveUiSelector selector)
        => LiveUiNodeQuery.OrderMatchesForSelection(
            LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry),
            capture.Payload,
            capture.Viewport);

    private static bool ObserveStableSample(
        LiveUiTreeCapture capture,
        LiveUiSelector selector,
        IReadOnlyList<LiveUiNodeMatch> matches,
        int requiredStableSamples,
        ref int stableSamples,
        ref string? previousHash,
        ref string? finalHash)
    {
        var stableNode = selector.HasCriteria
            ? selector.Select(matches)?.Node
            : capture.Root;
        if (stableNode is null)
        {
            stableSamples = 0;
            previousHash = null;
            return false;
        }

        finalHash = ComputeHash(stableNode);
        stableSamples = string.Equals(previousHash, finalHash, StringComparison.Ordinal)
            ? stableSamples + 1
            : 1;
        previousHash = finalHash;
        return stableSamples >= requiredStableSamples;
    }

    private static RequestResult BuildSuccess(
        string condition,
        LiveUiSelector selector,
        LiveUiTreeCapture capture,
        IReadOnlyList<LiveUiNodeMatch> matches,
        string evidenceSource,
        int attempts,
        long elapsedMs,
        string? treeHash)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "ui.wait_for",
                ["condition"] = condition,
                ["satisfied"] = true,
                ["sessionId"] = capture.Session.SessionId,
                ["appId"] = capture.Session.AppId,
                ["visualTreeToolId"] = capture.ToolId,
                ["evidenceSource"] = evidenceSource,
                ["capturedAtUtc"] = capture.CapturedAtUtc,
                ["selector"] = selector.ToJson(),
                ["attempts"] = attempts,
                ["elapsedMs"] = elapsedMs,
                ["matchCount"] = matches.Count,
                ["treeHash"] = treeHash,
                ["matches"] = new JsonArray(matches.Take(20).Select(LiveUiNodeQuery.ToResultJson).ToArray())
            },
            isError: false);
    }

    private static RequestResult BuildOcrSuccess(
        string condition,
        LiveUiSelector selector,
        AppSessionSnapshot session,
        LiveUiOcrSearchResult ocrResult,
        int attempts,
        long elapsedMs)
    {
        var matches = selector.ApplyIndex(ocrResult.Matches);
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "ui.wait_for",
                ["condition"] = condition,
                ["satisfied"] = true,
                ["sessionId"] = session.SessionId,
                ["appId"] = session.AppId,
                ["evidenceSource"] = "ocr",
                ["capturedAtUtc"] = DateTimeOffset.UtcNow,
                ["selector"] = selector.ToJson(),
                ["attempts"] = attempts,
                ["elapsedMs"] = elapsedMs,
                ["matchCount"] = matches.Count,
                ["treeHash"] = null,
                ["ocr"] = ocrResult.ToJson(),
                ["matches"] = new JsonArray(
                    matches.Take(20).Select(static match => match.DeepClone()).ToArray())
            },
            isError: false);
    }

    private void PersistTimelineTree(LiveUiTreeCapture capture)
    {
        SessionVisualTreePersistence.Persist(
            runtimeState,
            capture.Session,
            capture.ToolId,
            capture.RawPayload);
    }

    private static string ComputeHash(JsonObject node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
