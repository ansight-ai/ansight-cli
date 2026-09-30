using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class FindLiveUiTool : RemoteAppOperation
{
    private readonly ISessionScreenshotOcrScanner ocrScanner;

    public FindLiveUiTool(OperationServices services)
        : this(services, new TesseractSessionScreenshotOcrScanner())
    {
    }

    internal FindLiveUiTool(
        OperationServices services,
        ISessionScreenshotOcrScanner ocrScanner)
        : base(services)
    {
        this.ocrScanner = ocrScanner ?? throw new ArgumentNullException(nameof(ocrScanner));
    }

    public override string Name => "ansight_find_ui";

    protected override string Title => "Find Live UI";

    protected override string Description => "Find live app UI in safety order: device/OS accessibility, app framework semantics, screenshot OCR, then native visual-tree fallback. Supports exact, substring, and scored fuzzy discovery; fuzzy results remain read-only evidence and actions must use the returned exact target identity. Requested selector matches are returned separately from visibility-relaxed diagnostic matches. Every captured tree is retained as timeline evidence.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildFindProperties(),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }
        var session = snapshot!;

        var selector = LiveUiSelector.Parse(arguments, exactByDefault: false);
        if (!selector.HasCriteria)
        {
            return ToolError("At least one UI selector is required.");
        }

        var accessibilityResult = await LiveUiTreeCapture.CaptureForSelectorAsync(
            session,
            appToolBridge,
            uiInputRouter,
            Name,
            correlationId,
            selector,
            ToolExecutionCancellation.Current);
        var attemptedSources = accessibilityResult.AttemptedToolIds.ToList();
        var selectedCapture = accessibilityResult.Capture;
        if (selectedCapture is not null)
        {
            PersistTimelineTree(selectedCapture);
        }

        var limit = Math.Clamp(LiveUiToolSchemas.ReadInteger(arguments, "limit", 100), 1, 1_000);
        var semanticSelection = selectedCapture is null
            ? SemanticSelection.Empty
            : FindSemanticMatches(selectedCapture.Root, selectedCapture.TypeRegistry, selector);
        var evidenceSource = ResolveEvidenceSource(selectedCapture);
        var ocrResult = LiveUiOcrSearchResult.NotAttempted;
        if (semanticSelection.ResolutionMatches.Count == 0
            && selector.CanUseOcr
            && RunRequestContext.AllowsScreenshotOcr(correlationId))
        {
            attemptedSources.Add("ocr");
            ocrResult = await LiveUiOcrSearch.FindAsync(
                runtimeState,
                applicationPaths,
                appToolBridge,
                ocrScanner,
                session,
                selectedCapture?.Viewport,
                selector,
                "find-ui-ocr",
                correlationId,
                ToolExecutionCancellation.Current, externalScreenshots: externalScreenshots);
            if (ocrResult.Matches.Count > 0)
            {
                evidenceSource = "ocr";
            }
        }

        if (semanticSelection.ResolutionMatches.Count == 0 && ocrResult.Matches.Count == 0)
        {
            attemptedSources.Add("visualTree");
            var visualTreeResult = await LiveUiTreeCapture.CaptureNativeVisualTreeAsync(
                session,
                appToolBridge,
                Name,
                correlationId,
                ToolExecutionCancellation.Current);
            if (visualTreeResult.Capture is { } visualTreeCapture)
            {
                PersistTimelineTree(visualTreeCapture);
                selectedCapture = visualTreeCapture;
                semanticSelection = FindSemanticMatches(
                    visualTreeCapture.Root,
                    visualTreeCapture.TypeRegistry,
                    selector);
                evidenceSource = "visualTree";
            }
        }

        if (selectedCapture is null && ocrResult.Matches.Count == 0)
        {
            return ToolError(accessibilityResult.Message);
        }

        var viewport = selectedCapture?.Viewport;
        if (selectedCapture is not null)
        {
            semanticSelection = OrderSelection(
                semanticSelection,
                selectedCapture.Payload,
                viewport,
                selector);
        }
        var resolution = ocrResult.Matches.Count > 0
            ? "visible"
            : ResolveSemanticResolution(semanticSelection.ResolutionMatches, viewport);
        var semanticMatches = semanticSelection.StrictMatches
            .Select(match =>
            {
                var result = ToResultJson(match, viewport, evidenceSource, selector);
                result["tapHint"] = selectedCapture is null
                    ? null
                    : BuildTapHint(selectedCapture.Root, match, viewport, selector);
                return result;
            })
            .ToArray();
        var diagnosticSemanticMatches = semanticSelection.VisibilityRelaxed
            ? semanticSelection.ResolutionMatches
                .Select(match => ToResultJson(match, viewport, evidenceSource, selector))
                .ToArray()
            : [];
        var allResultMatches = ocrResult.Matches.Count > 0
            ? ocrResult.Matches
            : semanticMatches;
        IReadOnlyList<JsonObject> allDiagnosticMatches = ocrResult.Matches.Count > 0
            ? []
            : diagnosticSemanticMatches;
        var selectedResultMatches = selector.ApplyIndex(allResultMatches);
        var selectedDiagnosticMatches = selector.ApplyIndex(allDiagnosticMatches);
        var matches = new JsonArray(selectedResultMatches
            .Take(limit)
            .Select(static match => match.DeepClone())
            .ToArray());
        var diagnosticMatches = new JsonArray(selectedDiagnosticMatches
            .Take(limit)
            .Select(static match => match.DeepClone())
            .ToArray());
        var recoveryHint = ResolveRecoveryHint(resolution, selector.Text);
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "ui.find",
                ["sessionId"] = session.SessionId,
                ["appId"] = session.AppId,
                ["toolId"] = selectedCapture?.ToolId,
                ["evidenceSource"] = evidenceSource,
                ["attemptedSources"] = new JsonArray(
                    attemptedSources.Select(static source => JsonValue.Create(source)).ToArray()),
                ["fallbackAttempted"] = attemptedSources.Count > 1,
                ["capturedAtUtc"] = selectedCapture?.CapturedAtUtc ?? DateTimeOffset.UtcNow,
                ["selector"] = selector.ToJson(),
                ["resolution"] = resolution,
                ["selectorSatisfied"] = selectedResultMatches.Count > 0,
                ["visibilityRelaxed"] = semanticSelection.VisibilityRelaxed,
                ["offscreenPossible"] = resolution is "offscreen" or "notRepresented",
                ["recoveryHint"] = recoveryHint,
                ["ocr"] = ocrResult.ToJson(),
                ["matches"] = matches,
                ["diagnosticMatches"] = diagnosticMatches,
                ["count"] = matches.Count,
                ["totalMatches"] = allResultMatches.Count,
                ["truncated"] = !selector.IndexSpecified && allResultMatches.Count > matches.Count,
                ["diagnosticCount"] = diagnosticMatches.Count,
                ["totalDiagnosticMatches"] = allDiagnosticMatches.Count,
                ["diagnosticTruncated"] = !selector.IndexSpecified
                                            && allDiagnosticMatches.Count > diagnosticMatches.Count
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

    private static Dictionary<string, ToolSchema> BuildFindProperties()
    {
        var properties = LiveUiToolSchemas.SelectorProperties(
            new Dictionary<string, ToolSchema>
            {
                ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
                ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
                ["limit"] = ToolSchema.Integer("Maximum matches to return. Defaults to 100.", nullable: true)
            });
        properties["exact"] = ToolSchema.Boolean(
            "Deprecated compatibility switch: true maps to matchMode=exact and false maps to matchMode=contains. Ignored when matchMode is supplied.",
            nullable: true);
        properties["matchMode"] = ToolSchema.String(
            "String matching mode. exact requires equality, contains uses case-insensitive substring matching by default, and fuzzy performs conservative normalized typo/token discovery with scored results. Defaults to contains. Fuzzy matching never relaxes nodeId and must not be used directly for actions.",
            enumValues: ["exact", "contains", "fuzzy"],
            nullable: true);
        return properties;
    }

    private static string ResolveEvidenceSource(LiveUiTreeCapture? capture)
        => string.Equals(
            capture?.ToolId,
            LiveUiTreeCapture.DeviceAccessibilityToolId,
            StringComparison.Ordinal)
            ? "deviceAccessibility"
            : "frameworkVisualTree";

    private static JsonObject ToResultJson(
        LiveUiNodeMatch match,
        LiveUiBounds? viewport,
        string evidenceSource,
        LiveUiSelector selector)
    {
        var result = LiveUiNodeQuery.ToResultJson(match);
        var relation = ResolveViewportRelation(LiveUiNodeQuery.ReadBounds(match.Node), viewport);
        result["source"] = evidenceSource;
        result["viewportRelation"] = relation;
        result["onScreen"] = (relation is "inside" or "partial" or "unknown")
                             && LiveUiNodeQuery.IsEffectivelyVisible(match);
        if (selector.MatchMode == LiveUiStringMatchMode.Fuzzy)
        {
            var evaluation = selector.Evaluate(match);
            result["matchMode"] = "fuzzy";
            result["matchScore"] = evaluation.Score;
            result["matchReason"] = evaluation.Reason;
        }
        return result;
    }

    internal static SemanticSelection FindSemanticMatches(
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry,
        LiveUiSelector selector)
    {
        var matches = LiveUiNodeQuery.Find(root, selector, typeRegistry);
        if (matches.Count > 0 || selector.Visible != true)
        {
            return new SemanticSelection(matches, matches, false);
        }

        var relaxedMatches = LiveUiNodeQuery.Find(
            root,
            selector.WithoutVisibility(),
            typeRegistry);
        return new SemanticSelection(relaxedMatches, matches, relaxedMatches.Count > 0);
    }

    internal static JsonObject? BuildTapHint(
        JsonObject root,
        LiveUiNodeMatch match,
        LiveUiBounds? viewport,
        LiveUiSelector originalSelector)
    {
        var bounds = LiveUiNodeQuery.ReadBounds(match.Node);
        if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0
            || !LiveUiNodeQuery.IsEffectivelyVisible(match)
            || !LiveUiNodeQuery.IsEffectivelyEnabled(match)
            || ResolveViewportRelation(bounds, viewport) is not ("inside" or "partial"))
        {
            return null;
        }

        var selector = new JsonObject { ["exact"] = true, ["visible"] = true, ["enabled"] = true };
        if (LiveUiNodeQuery.ReadAutomationId(match.Node) is { Length: > 0 } automationId)
        {
            selector["automationId"] = automationId;
        }
        else if (LiveUiNodeQuery.ReadString(match.Node, "id") is { Length: > 0 } nodeId)
        {
            selector["nodeId"] = nodeId;
        }
        else if (LiveUiNodeQuery.ReadText(match.Node) is { Length: > 0 } text)
        {
            selector["text"] = text;
            selector["role"] = LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry);
            selector["type"] = match.TypeRegistry.Resolve(match.Node);
        }
        else
        {
            return null;
        }

        if (originalSelector.AncestorAutomationId is not null)
        {
            selector["ancestorAutomationId"] = originalSelector.AncestorAutomationId;
        }

        // Validate against the full action tree. A broad-query index cannot be carried into
        // narrowed filters, and duplicate rows must not receive an invented positional target.
        var resolved = LiveUiNodeQuery.Find(root, LiveUiSelector.Parse(selector), match.TypeRegistry);
        return resolved.Count == 1 && ReferenceEquals(resolved[0].Node, match.Node)
            ? new JsonObject { ["tool"] = "ansight_tap_ui", ["selector"] = selector }
            : null;
    }

    private static SemanticSelection OrderSelection(
        SemanticSelection selection,
        JsonObject payload,
        LiveUiBounds? viewport,
        LiveUiSelector selector)
        => new(
            OrderMatches(
                selection.ResolutionMatches,
                payload,
                viewport,
                selector),
            OrderMatches(
                selection.StrictMatches,
                payload,
                viewport,
                selector),
            selection.VisibilityRelaxed);

    private static IReadOnlyList<LiveUiNodeMatch> OrderMatches(
        IReadOnlyList<LiveUiNodeMatch> matches,
        JsonObject payload,
        LiveUiBounds? viewport,
        LiveUiSelector selector)
    {
        var visualOrder = LiveUiNodeQuery.OrderMatchesForSelection(matches, payload, viewport);
        if (selector.MatchMode != LiveUiStringMatchMode.Fuzzy)
        {
            return visualOrder;
        }

        return visualOrder
            .Select((match, index) => new LiveUiScoredNodeMatch(
                match,
                index,
                selector.Evaluate(match).Score))
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.VisualOrder)
            .Select(static candidate => candidate.Match)
            .ToArray();
    }

    internal static string ResolveSemanticResolution(
        IReadOnlyList<LiveUiNodeMatch> matches,
        LiveUiBounds? viewport)
    {
        if (matches.Count == 0)
        {
            return "notRepresented";
        }

        if (matches.Any(match =>
                LiveUiNodeQuery.IsEffectivelyVisible(match)
                && (ResolveViewportRelation(LiveUiNodeQuery.ReadBounds(match.Node), viewport)
                    is "inside" or "partial" or "unknown")))
        {
            return "visible";
        }

        if (matches.Any(match =>
                (ResolveViewportRelation(LiveUiNodeQuery.ReadBounds(match.Node), viewport)
                    is "above" or "below" or "left" or "right" or "outside")))
        {
            return "offscreen";
        }

        return "hidden";
    }

    internal static string ResolveViewportRelation(
        LiveUiBounds? bounds,
        LiveUiBounds? viewport)
    {
        if (bounds is null || viewport is null)
        {
            return "unknown";
        }

        var boundsRight = bounds.X + bounds.Width;
        var boundsBottom = bounds.Y + bounds.Height;
        var viewportRight = viewport.X + viewport.Width;
        var viewportBottom = viewport.Y + viewport.Height;
        if (boundsBottom <= viewport.Y)
        {
            return "above";
        }
        if (bounds.Y >= viewportBottom)
        {
            return "below";
        }
        if (boundsRight <= viewport.X)
        {
            return "left";
        }
        if (bounds.X >= viewportRight)
        {
            return "right";
        }

        var fullyInside = bounds.X >= viewport.X
                          && bounds.Y >= viewport.Y
                          && boundsRight <= viewportRight
                          && boundsBottom <= viewportBottom;
        return fullyInside ? "inside" : "partial";
    }

    private static string? ResolveRecoveryHint(string resolution, string? requestedText)
    {
        var target = string.IsNullOrWhiteSpace(requestedText)
            ? "the exact target"
            : $"'{requestedText}'";
        return resolution switch
        {
            "offscreen" => $"{target} exists outside the current viewport. Scroll the nearest stable scroll container toward the reported viewportRelation, then retry the same selector.",
            "hidden" => $"{target} is represented but hidden. Wait for or perform the state change that makes it visible before acting.",
            "notRepresented" => $"The current framework, accessibility, and available OCR evidence did not represent {target}. It may be virtualized or off-screen. Identify the relevant scroll container, perform one bounded scroll, and retry; do not treat this result alone as proof of absence.",
            _ => null
        };
    }

    internal sealed record SemanticSelection(
        IReadOnlyList<LiveUiNodeMatch> ResolutionMatches,
        IReadOnlyList<LiveUiNodeMatch> StrictMatches,
        bool VisibilityRelaxed)
    {
        public static SemanticSelection Empty { get; } = new([], [], false);
    }

    private sealed record LiveUiScoredNodeMatch(
        LiveUiNodeMatch Match,
        int VisualOrder,
        double Score);
}
