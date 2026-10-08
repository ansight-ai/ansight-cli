using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal enum LiveUiActionKind
{
    Tap,
    TypeText,
    Swipe,
    Scroll,
    Pinch,
    Back,
    KeyboardOpen,
    KeyboardDismiss
}

internal readonly record struct LiveUiScreenPoint(double X, double Y);

internal readonly record struct LiveUiSwipePath(
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    int DurationMilliseconds);

internal readonly record struct LiveUiPinchPath(
    double PrimaryStartX,
    double PrimaryStartY,
    double SecondaryStartX,
    double SecondaryStartY,
    double PrimaryEndX,
    double PrimaryEndY,
    double SecondaryEndX,
    double SecondaryEndY,
    int DurationMilliseconds);

internal sealed class LiveUiActionTool : RemoteAppOperation
{
    private readonly LiveUiActionKind actionKind;
    private readonly ISessionScreenshotOcrScanner ocrScanner;

    public LiveUiActionTool(OperationServices services, LiveUiActionKind actionKind)
        : this(services, actionKind, new TesseractSessionScreenshotOcrScanner())
    {
    }

    internal LiveUiActionTool(
        OperationServices services,
        LiveUiActionKind actionKind,
        ISessionScreenshotOcrScanner ocrScanner)
        : base(services)
    {
        this.actionKind = actionKind;
        this.ocrScanner = ocrScanner ?? throw new ArgumentNullException(nameof(ocrScanner));
    }

    public override string Name => actionKind switch
    {
        LiveUiActionKind.Tap => "ansight_tap_ui",
        LiveUiActionKind.TypeText => "ansight_type_text",
        LiveUiActionKind.Swipe => "ansight_swipe_ui",
        LiveUiActionKind.Scroll => "ansight_scroll_ui",
        LiveUiActionKind.Pinch => "ansight_pinch_ui",
        LiveUiActionKind.Back => "ansight_back_ui",
        LiveUiActionKind.KeyboardOpen => "ansight_open_keyboard",
        LiveUiActionKind.KeyboardDismiss => "ansight_dismiss_keyboard",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override string Title => actionKind switch
    {
        LiveUiActionKind.Tap => "Tap Live UI",
        LiveUiActionKind.TypeText => "Type Text Into Live UI",
        LiveUiActionKind.Swipe => "Swipe Live UI",
        LiveUiActionKind.Scroll => "Scroll Live UI",
        LiveUiActionKind.Pinch => "Pinch Live UI",
        LiveUiActionKind.Back => "Navigate Back In Live UI",
        LiveUiActionKind.KeyboardOpen => "Open The Software Keyboard",
        LiveUiActionKind.KeyboardDismiss => "Dismiss The Software Keyboard",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override string Description => actionKind switch
    {
        LiveUiActionKind.Tap => "Tap a resolved live semantic target, one unique exact screenshot-text match, or an exact recorded viewport-normalized point through simulator or emulator input.",
        LiveUiActionKind.TypeText => "Resolve and focus a live visual-tree target, then type text through simulator or emulator input.",
        LiveUiActionKind.Swipe => "Perform an exact recorded path or directional finger swipe through Ansight's simulator or emulator input host.",
        LiveUiActionKind.Scroll => "Scroll toward content above, below, left, or right using a synthesized mobile swipe. Down reveals content below with an upward finger swipe; up reveals content above with a downward finger swipe.",
        LiveUiActionKind.Pinch => "Perform a two-contact pinch around a viewport point through Ansight's simulator or emulator input host.",
        LiveUiActionKind.Back => "Press the platform back control through Ansight's simulator or emulator input host.",
        LiveUiActionKind.KeyboardOpen => "Focus a resolved live text-input target and verify that the system software keyboard opens.",
        LiveUiActionKind.KeyboardDismiss => "Dismiss the system software keyboard when fresh device accessibility confirms that it is open.",
        _ => throw new ArgumentOutOfRangeException()
    };

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildInputProperties(),
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var deviceIdentifier = uiInputRouter.ResolveDeviceIdentifier(
            snapshot!.SessionId,
            DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot));
        if (deviceIdentifier is null)
        {
            return ToolError(
                "The live session does not report device.nativeDeviceId, so Ansight cannot safely target simulator input.");
        }

        var availability = uiInputRouter.GetAvailability(deviceIdentifier);
        if (!availability.IsAvailable)
        {
            return ToolError(availability.Message);
        }

        var preflightResult = await uiInputRouter.PrepareForInputAsync(
            new UiInputPreflightRequest(snapshot.SessionId, deviceIdentifier, snapshot.AppId),
            ToolExecutionCancellation.Current);
        if (!preflightResult.IsSuccess)
        {
            return ToolError(preflightResult.Message);
        }

        var selector = LiveUiSelector.Parse(arguments);
        var captureResult = actionKind is LiveUiActionKind.Back or LiveUiActionKind.KeyboardDismiss
            ? await LiveUiTreeCapture.CaptureAsync(
                snapshot!,
                appToolBridge,
                uiInputRouter,
                Name,
                correlationId,
                ToolExecutionCancellation.Current)
            : await LiveUiTreeCapture.CaptureForSelectorAsync(
                snapshot!,
                appToolBridge,
                uiInputRouter,
                Name,
                correlationId,
                selector,
                ToolExecutionCancellation.Current,
                allowCached: arguments?["targetFingerprint"] is null);
        if (!captureResult.IsSuccess || captureResult.Capture is null)
        {
            return ToolError(captureResult.Message);
        }

        var capture = captureResult.Capture;
        var includeScreenshot = arguments?["includeScreenshot"]?.GetValue<bool>() ?? true;
        return actionKind is LiveUiActionKind.Back or LiveUiActionKind.KeyboardDismiss
            ? await ExecuteBackAsync(capture, deviceIdentifier, includeScreenshot, correlationId)
            : await ExecuteTargetedActionAsync(
                capture,
                deviceIdentifier,
                selector,
                arguments,
                includeScreenshot,
                correlationId);
    }

    private async Task<RequestResult> ExecuteTargetedActionAsync(
        LiveUiTreeCapture capture,
        string deviceIdentifier,
        LiveUiSelector selector,
        JsonObject? arguments,
        bool includeScreenshot,
        string? correlationId)
    {
        var hasRecordedTapPoint = actionKind == LiveUiActionKind.Tap
                                  && (arguments?.ContainsKey("normalizedX") == true
                                      || arguments?.ContainsKey("normalizedY") == true);
        var requiresTarget = actionKind is LiveUiActionKind.TypeText or LiveUiActionKind.KeyboardOpen
                             || (actionKind == LiveUiActionKind.Tap && !hasRecordedTapPoint);
        if (requiresTarget && !selector.HasCriteria)
        {
            return ToolError(
                actionKind == LiveUiActionKind.Tap
                    ? "A stable UI selector or a recorded normalizedX/normalizedY viewport point is required for this action."
                    : "A stable UI selector is required for this action.");
        }

        var hasTextValue = LiveUiToolSchemas.TryReadRawString(arguments, "value", out var textValue);
        var replaceExisting = actionKind == LiveUiActionKind.TypeText
            && (arguments?["replaceExisting"]?.GetValue<bool>() ?? true);
        if (actionKind == LiveUiActionKind.TypeText && !hasTextValue)
        {
            return ToolError("value is required for text input.");
        }

        var viewport = capture.Viewport;
        if (viewport is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return ToolError("The visual tree does not expose usable screen-space bounds for host input.");
        }

        LiveUiNodeMatch? target = null;
        LiveUiScreenPoint? ocrNormalizedPoint = null;
        JsonObject? ocrTarget = null;
        JsonObject? ocrTraceEvidence = null;
        if (selector.HasCriteria)
        {
            var matches = LiveUiNodeQuery.PrioritizeActionTargets(
                LiveUiNodeQuery.Find(capture.Root, selector, capture.TypeRegistry),
                capture.Payload,
                viewport);
            if (matches.Count == 0)
            {
                var canUseOcr = arguments?["targetFingerprint"] is null && selector.CanUseOcr
                                && RunRequestContext.AllowsScreenshotOcr(correlationId);
                if (actionKind == LiveUiActionKind.Tap && canUseOcr)
                {
                    var ocrResult = await LiveUiOcrSearch.FindAsync(
                        runtimeState,
                        applicationPaths,
                        appToolBridge,
                        ocrScanner,
                        capture.Session,
                        capture.Viewport,
                        selector,
                        "tap-ui-ocr",
                        correlationId,
                        ToolExecutionCancellation.Current, externalScreenshots: externalScreenshots);
                    ocrTraceEvidence = ocrResult.TraceEvidence;
                    if (ocrResult.TryGetTapPoint(
                            selector.Index,
                            selector.IndexSpecified,
                            out var ocrPoint,
                            out ocrTarget))
                    {
                        ocrNormalizedPoint = ocrPoint;
                    }
                    else if (ocrResult.Matches.Count > 1 && !selector.IndexSpecified)
                    {
                        return BuildSelectorError(
                            $"Screenshot OCR found {ocrResult.Matches.Count} visible text matches. Use ansight_find_ui to disambiguate the exact current target before tapping.",
                            selector, ocrTraceEvidence);
                    }
                    else if (selector.IndexSpecified && selector.Index >= ocrResult.Matches.Count)
                    {
                        return BuildSelectorError(
                            $"Screenshot OCR found {ocrResult.Matches.Count} visible text match(es), but index {selector.Index} was requested.",
                            selector, ocrTraceEvidence);
                    }
                }

                if (ocrNormalizedPoint is null && selector.Visible == true
                    && LiveUiNodeQuery.Find(
                        capture.Root,
                        selector.WithoutVisibility(),
                        capture.TypeRegistry).Count > 0)
                {
                    return BuildSelectorError(
                        "A live UI node matched the stable selector fields, but it is effectively hidden. One of its MAUI ancestors may no longer be visible.",
                        selector, ocrTraceEvidence);
                }

                if (ocrNormalizedPoint is null)
                {
                    return BuildSelectorError(
                        canUseOcr
                            ? "No semantic node or unique current screenshot-text match satisfied the supplied selector."
                            : "No live UI node matched the supplied selector.",
                        selector, ocrTraceEvidence);
                }
            }

            if (matches.Count > 0 && selector.Index >= matches.Count)
            {
                return BuildSelectorError(
                    $"The selector matched {matches.Count} node(s), but index {selector.Index} was requested. "
                    + "Indexes apply after all selector filters. Reuse the exact tapHint from a fresh find result.",
                    selector, ocrTraceEvidence);
            }

            if (matches.Count > 0)
            {
                target = matches[selector.Index];
                if (!LiveUiNodeQuery.IsEffectivelyVisible(target)
                    || !LiveUiNodeQuery.IsEffectivelyEnabled(target))
                {
                    return BuildSelectorError("The selected UI node is not visible and enabled.", selector, ocrTraceEvidence);
                }
                if (actionKind == LiveUiActionKind.KeyboardOpen
                    && !string.Equals(
                        LiveUiNodeQuery.ReadRole(target.Node, target.TypeRegistry),
                        "textbox",
                        StringComparison.OrdinalIgnoreCase)
                    && !LiveUiNodeQuery.ReadSupportedActions(target.Node, target.TypeRegistry)
                        .Any(action => string.Equals(action, "focus", StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(action, "typeText", StringComparison.OrdinalIgnoreCase)))
                {
                    return BuildSelectorError(
                        "The selected UI node is not reported as a text input that can receive focus.", selector, ocrTraceEvidence);
                }
            }
        }

        if (arguments?["targetFingerprint"] is { } fingerprintValue)
        {
            if (fingerprintValue is not JsonValue fingerprintScalar
                || !fingerprintScalar.TryGetValue<string>(out var fingerprint)
                || target is null
                || !string.Equals(fingerprint, LiveUiTapTarget.Fingerprint(capture.Root, target, viewport), StringComparison.Ordinal))
            {
                return BuildSelectorError(
                    "The discovered target or its nearby content changed. No input was delivered. "
                    + "Run ansight_find_ui again and use the selected result's complete fresh tapHint.", selector, ocrTraceEvidence);
            }
        }

        if (actionKind == LiveUiActionKind.KeyboardOpen)
        {
            var keyboardStateCapture = await LiveUiTreeCapture.CaptureDeviceAccessibilityAsync(
                capture.Session,
                uiInputRouter,
                cancellationToken: ToolExecutionCancellation.Current,
                allowCached: false).ConfigureAwait(false);
            if (!keyboardStateCapture.IsSuccess
                || !LiveKeyboardState.TryRead(keyboardStateCapture.Capture, out _))
            {
                return ToolError(
                    "The keyboard cannot be opened verifiably because fresh device accessibility did not report keyboard visibility.");
            }
        }

        var targetBounds = target is null ? viewport : LiveUiNodeQuery.ReadBounds(target.Node);
        if (targetBounds is null
            || targetBounds.Width <= 0
            || targetBounds.Height <= 0
            || target is not null && LiveUiBounds.Intersection(targetBounds, viewport) is null)
        {
            return ToolError("The selected UI node does not expose usable screen-space bounds within the current viewport.");
        }

        if (actionKind == LiveUiActionKind.Tap
            && target is not null
            && !HasExplicitTapPoint(arguments)
            && IsUnsafeImplicitTapTarget(target, targetBounds, viewport))
        {
            return ToolError(
                "The selector resolved to a non-actionable semantic container covering nearly the whole viewport. "
                + "No input was delivered. Use a focused actionable selector or an exact rendered-text tap hint.");
        }

        var inputPoint = new LiveUiScreenPoint(targetBounds.CenterX, targetBounds.CenterY);
        if (actionKind == LiveUiActionKind.Tap && ocrNormalizedPoint is { } normalizedOcrPoint)
        {
            inputPoint = new LiveUiScreenPoint(
                viewport.X + (normalizedOcrPoint.X * viewport.Width),
                viewport.Y + (normalizedOcrPoint.Y * viewport.Height));
        }
        else if (actionKind == LiveUiActionKind.Tap
            && !TryResolveTapPoint(arguments, targetBounds, viewport, out inputPoint, out var tapPointError))
        {
            return ToolError(tapPointError);
        }

        var actionId = $"ui-action-{Guid.NewGuid():N}";
        var beforeEvidence = await LiveUiActionEvidence.CaptureAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            capture,
            actionId,
            Capability,
            "before",
            correlationId,
            ToolExecutionCancellation.Current,
            captureScreenshot: includeScreenshot, externalScreenshots: externalScreenshots);
        RecordActionLog(
            capture.Session.SessionId,
            actionId,
            $"Starting {Capability} through Ansight host input.",
            LogPriority.Information);
        UiInputResult inputResult;
        switch (actionKind)
        {
            case LiveUiActionKind.Tap:
            case LiveUiActionKind.KeyboardOpen:
                inputResult = await uiInputRouter.TapAsync(
                    new UiTapRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        NormalizeX(inputPoint.X, viewport),
                        NormalizeY(inputPoint.Y, viewport),
                        capture.Session.AppId),
                    ToolExecutionCancellation.Current);
                break;
            case LiveUiActionKind.TypeText:
                var focusResult = await uiInputRouter.TapAsync(
                    new UiTapRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        NormalizeX(targetBounds.CenterX, viewport),
                        NormalizeY(targetBounds.CenterY, viewport),
                        capture.Session.AppId),
                    ToolExecutionCancellation.Current);
                if (!focusResult.IsSuccess)
                {
                    var failedFocusEvidence = await CaptureAfterEvidenceAsync(
                        capture,
                        actionId,
                        Capability,
                        correlationId,
                        includeScreenshot);
                    RecordActionLog(
                        capture.Session.SessionId,
                        actionId,
                        $"{Capability} failed while focusing the target: {focusResult.Message}",
                        LogPriority.Error);
                    return BuildActionResult(
                        capture,
                        actionId,
                        target,
                        focusResult,
                        "focus",
                        beforeEvidence,
                        failedFocusEvidence.Evidence,
                        includeScreenshot);
                }

                inputResult = await uiInputRouter.TypeTextAsync(
                    new UiTextRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        textValue,
                        replaceExisting,
                        capture.Session.AppId),
                    ToolExecutionCancellation.Current);
                break;
            case LiveUiActionKind.Swipe:
            case LiveUiActionKind.Scroll:
                inputResult = await ExecuteSwipeAsync(
                    capture,
                    deviceIdentifier,
                    target,
                    targetBounds,
                    viewport,
                    arguments);
                break;
            case LiveUiActionKind.Pinch:
                inputResult = await ExecutePinchAsync(
                    capture,
                    deviceIdentifier,
                    target,
                    targetBounds,
                    viewport,
                    arguments);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var afterCapture = await CaptureAfterEvidenceAsync(
            capture,
            actionId,
            Capability,
            correlationId,
            includeScreenshot);
        bool? keyboardOpen = null;
        string? postconditionError = null;
        if (actionKind == LiveUiActionKind.Scroll
            && inputResult.IsSuccess
            && afterCapture.Capture is not null)
        {
            postconditionError = LiveUiMauiContext.DescribeUnexpectedPageChange(
                capture.Payload,
                capture.TypeRegistry,
                afterCapture.Capture.Payload,
                afterCapture.Capture.TypeRegistry);
        }
        else if (actionKind == LiveUiActionKind.KeyboardOpen && inputResult.IsSuccess)
        {
            if (!LiveKeyboardState.TryRead(afterCapture.Capture, out var observedKeyboardOpen))
            {
                postconditionError =
                    "The text input was focused, but fresh device accessibility could not confirm whether the software keyboard opened.";
            }
            else
            {
                keyboardOpen = observedKeyboardOpen;
                if (!observedKeyboardOpen)
                {
                    postconditionError =
                        "The text input was focused, but the software keyboard did not open.";
                }
            }
        }
        RecordActionLog(
            capture.Session.SessionId,
            actionId,
            postconditionError is not null
                ? postconditionError
                : inputResult.IsSuccess
                ? $"Completed {Capability} through {inputResult.Backend}."
                : $"{Capability} failed: {inputResult.Message}",
            inputResult.IsSuccess && postconditionError is null
                ? LogPriority.Information
                : LogPriority.Error);
        return BuildActionResult(
            capture,
            actionId,
            target,
            inputResult,
            Capability,
            beforeEvidence,
            afterCapture.Evidence,
            includeScreenshot,
            postconditionError,
            ocrTarget,
            ocrTraceEvidence,
            keyboardOpen);
    }

    internal static bool IsUnsafeImplicitTapTarget(
        LiveUiNodeMatch target,
        LiveUiBounds targetBounds,
        LiveUiBounds viewport)
    {
        var intersection = LiveUiBounds.Intersection(targetBounds, viewport);
        if (intersection is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return false;
        }

        var viewportArea = viewport.Width * viewport.Height;
        var targetArea = intersection.Width * intersection.Height;
        var supportedActions = LiveUiNodeQuery.ReadSupportedActions(target.Node, target.TypeRegistry);
        return targetArea / viewportArea >= 0.9
               && !supportedActions.Contains("tap", StringComparer.OrdinalIgnoreCase)
               && LiveUiNodeQuery.ReadRole(target.Node, target.TypeRegistry) is "view" or "application";
    }

    private static bool HasExplicitTapPoint(JsonObject? arguments)
        => arguments?.ContainsKey("normalizedX") == true
           || arguments?.ContainsKey("normalizedY") == true
           || arguments?.ContainsKey("screenX") == true
           || arguments?.ContainsKey("screenY") == true
           || arguments?.ContainsKey("targetX") == true
           || arguments?.ContainsKey("targetY") == true;

    private async Task<UiInputResult> ExecuteSwipeAsync(
        LiveUiTreeCapture capture,
        string deviceIdentifier,
        LiveUiNodeMatch? target,
        LiveUiBounds targetBounds,
        LiveUiBounds viewport,
        JsonObject? arguments)
    {
        var gestureBounds = LiveUiMauiContext.ResolveGestureBounds(
            capture.Payload,
            capture.Root,
            capture.TypeRegistry,
            target,
            targetBounds,
            viewport);
        if (!TryResolveSwipePath(
                arguments,
                gestureBounds,
                viewport,
                actionKind == LiveUiActionKind.Scroll,
                out var path,
                out var error))
        {
            return UiInputResult.Failure(error);
        }

        return await uiInputRouter.SwipeAsync(
            new UiSwipeRequest(
                capture.Session.SessionId,
                deviceIdentifier,
                path.StartX,
                path.StartY,
                path.EndX,
                path.EndY,
                path.DurationMilliseconds,
                capture.Session.AppId),
            ToolExecutionCancellation.Current);
    }

    private async Task<UiInputResult> ExecutePinchAsync(
        LiveUiTreeCapture capture,
        string deviceIdentifier,
        LiveUiNodeMatch? target,
        LiveUiBounds targetBounds,
        LiveUiBounds viewport,
        JsonObject? arguments)
    {
        var gestureBounds = LiveUiMauiContext.ResolveGestureBounds(
            capture.Payload,
            capture.Root,
            capture.TypeRegistry,
            target,
            targetBounds,
            viewport);
        if (!TryResolvePinchPath(arguments, gestureBounds, viewport, out var path, out var error))
        {
            return UiInputResult.Failure(error);
        }

        return await uiInputRouter.PinchAsync(
            new UiPinchRequest(
                capture.Session.SessionId,
                deviceIdentifier,
                path.PrimaryStartX,
                path.PrimaryStartY,
                path.SecondaryStartX,
                path.SecondaryStartY,
                path.PrimaryEndX,
                path.PrimaryEndY,
                path.SecondaryEndX,
                path.SecondaryEndY,
                path.DurationMilliseconds,
                capture.Session.AppId),
            ToolExecutionCancellation.Current);
    }

    private async Task<RequestResult> ExecuteBackAsync(
        LiveUiTreeCapture capture,
        string deviceIdentifier,
        bool includeScreenshot,
        string? correlationId)
    {
        var isKeyboardDismiss = actionKind == LiveUiActionKind.KeyboardDismiss;
        bool? keyboardOpenBefore = null;
        if (isKeyboardDismiss)
        {
            if (!LiveKeyboardState.TryRead(capture, out var observedKeyboardOpen))
            {
                return ToolError(
                    "The keyboard could not be dismissed safely because fresh device accessibility did not report its visibility.");
            }

            keyboardOpenBefore = observedKeyboardOpen;
        }

        var actionId = $"ui-action-{Guid.NewGuid():N}";
        var beforeEvidence = await LiveUiActionEvidence.CaptureAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            capture,
            actionId,
            Capability,
            "before",
            correlationId,
            ToolExecutionCancellation.Current,
            captureScreenshot: includeScreenshot, externalScreenshots: externalScreenshots);
        RecordActionLog(
            capture.Session.SessionId,
            actionId,
            $"Starting {Capability} through Ansight host input.",
            LogPriority.Information);
        var result = keyboardOpenBefore == false
            ? new UiInputResult(
                true,
                "none",
                "The system software keyboard is already dismissed.")
            : await uiInputRouter.PressButtonAsync(
                new UiButtonRequest(capture.Session.SessionId, deviceIdentifier, "back", capture.Session.AppId),
                ToolExecutionCancellation.Current);
        var afterCapture = await CaptureAfterEvidenceAsync(
            capture,
            actionId,
            Capability,
            correlationId,
            includeScreenshot);
        bool? keyboardOpenAfter = null;
        string? postconditionError = null;
        if (isKeyboardDismiss && result.IsSuccess)
        {
            if (!LiveKeyboardState.TryRead(afterCapture.Capture, out var observedKeyboardOpen))
            {
                postconditionError =
                    "The dismiss input completed, but fresh device accessibility could not confirm whether the software keyboard closed.";
            }
            else
            {
                keyboardOpenAfter = observedKeyboardOpen;
                if (observedKeyboardOpen)
                {
                    postconditionError = "The software keyboard remained open after the dismiss input.";
                }
            }
        }

        RecordActionLog(
            capture.Session.SessionId,
            actionId,
            postconditionError is not null
                ? postconditionError
                : result.IsSuccess
                ? $"Completed {Capability} through {result.Backend}."
                : $"{Capability} failed: {result.Message}",
            result.IsSuccess && postconditionError is null
                ? LogPriority.Information
                : LogPriority.Error);
        return BuildActionResult(
            capture,
            actionId,
            target: null,
            result,
            Capability,
            beforeEvidence,
            afterCapture.Evidence,
            includeScreenshot,
            postconditionError,
            keyboardOpen: keyboardOpenAfter,
            performed: keyboardOpenBefore != false && result.IsSuccess);
    }

    private async Task<AfterActionCapture> CaptureAfterEvidenceAsync(
        LiveUiTreeCapture beforeCapture,
        string actionId,
        string capability,
        string? correlationId,
        bool captureScreenshot)
        => await LiveUiEvidenceCapture.CaptureAfterAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            uiInputRouter,
            beforeCapture,
            actionId,
            capability,
            Name,
            correlationId,
            cancellationToken: ToolExecutionCancellation.Current,
            captureScreenshot: captureScreenshot, externalScreenshots: externalScreenshots);

    private static RequestResult BuildActionResult(
        LiveUiTreeCapture capture,
        string actionId,
        LiveUiNodeMatch? target,
        UiInputResult inputResult,
        string capability,
        ActionEvidenceCapture beforeEvidence,
        ActionEvidenceCapture afterEvidence,
        bool includeScreenshot,
        string? postconditionError = null,
        JsonObject? screenTarget = null,
        JsonObject? ocrTraceEvidence = null,
        bool? keyboardOpen = null,
        bool? performed = null)
    {
        var message = postconditionError ?? inputResult.Message;
        var structuredContent = new JsonObject
        {
            ["actionId"] = actionId,
            ["capability"] = capability,
            ["performed"] = performed ?? inputResult.IsSuccess,
            ["sessionId"] = capture.Session.SessionId,
            ["appId"] = capture.Session.AppId,
            ["deviceIdentifier"] = capture.ResolveNativeDeviceIdentifier(),
            ["visualTreeToolId"] = capture.ToolId,
            ["capturedAtUtc"] = capture.CapturedAtUtc,
            ["inputBackend"] = inputResult.Backend,
            ["message"] = message,
            ["postcondition"] = postconditionError is null
                ? null
                : new JsonObject
                {
                    ["succeeded"] = false,
                    ["message"] = postconditionError
                },
            ["target"] = screenTarget?.DeepClone()
                         ?? (target is null ? null : LiveUiNodeQuery.ToResultJson(target)),
            ["evidence"] = new JsonObject
            {
                ["before"] = LiveUiActionEvidence.ToJson(beforeEvidence),
                ["after"] = LiveUiActionEvidence.ToJson(afterEvidence)
            }
        };
        if (ocrTraceEvidence is not null)
        {
            structuredContent[LiveUiOcrTraceEvidence.PayloadPropertyName] = ocrTraceEvidence.DeepClone();
        }
        if (keyboardOpen.HasValue)
        {
            structuredContent["isOpen"] = keyboardOpen.Value;
        }
        return LiveUiActionResponse.Build(
            structuredContent,
            afterEvidence,
            includeScreenshot,
            isError: !inputResult.IsSuccess || postconditionError is not null);
    }

    internal static RequestResult BuildSelectorError(
        string message,
        LiveUiSelector selector,
        JsonObject? ocrTraceEvidence)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = selector.DescribeFailure(message),
                ["selector"] = selector.ToJson(),
                ["performed"] = false,
                [LiveUiOcrTraceEvidence.PayloadPropertyName] = ocrTraceEvidence?.DeepClone()
            },
            isError: true);
    }

    private Dictionary<string, ToolSchema> BuildInputProperties()
    {
        var properties = new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["includeScreenshot"] = ToolSchema.Boolean("Include the final screenshot as image content. Defaults to true.", nullable: true)
        };
        if (actionKind is LiveUiActionKind.KeyboardOpen or LiveUiActionKind.KeyboardDismiss)
        {
            properties["deviceId"] = ToolSchema.String(
                "Native device identifier used to select a matching live app session. May be combined with appId.",
                nullable: true);
        }

        if (actionKind is not LiveUiActionKind.Back and not LiveUiActionKind.KeyboardDismiss)
        {
            LiveUiToolSchemas.SelectorProperties(properties);
        }

        if (actionKind == LiveUiActionKind.TypeText)
        {
            properties["value"] = ToolSchema.String("Text to type after focusing the target.");
            properties["replaceExisting"] = ToolSchema.Boolean(
                "Select and replace the target's existing text before typing. Defaults to true; set false only to append.",
                nullable: true);
        }

        if (actionKind is LiveUiActionKind.Tap or LiveUiActionKind.TypeText)
        {
            properties["targetFingerprint"] = ToolSchema.String(
                "Copy this opaque freshness value from tapHint.selector when supplied. The host rechecks the target and nearby text before acting; rediscover if it changed.",
                nullable: true);
        }
        if (actionKind == LiveUiActionKind.Tap)
        {
            properties["normalizedX"] = ToolSchema.Number(
                "Optional recorded viewport-normalized X coordinate from 0 through 1. Must be supplied with normalizedY. A selector is not required for this recorded-coordinate form.",
                nullable: true);
            properties["normalizedY"] = ToolSchema.Number(
                "Optional recorded viewport-normalized Y coordinate from 0 through 1. Must be supplied with normalizedX. A selector is not required for this recorded-coordinate form.",
                nullable: true);
            properties["screenX"] = ToolSchema.Number(
                "Optional exact screen-space X coordinate returned by a trusted app inspection tool. Must be supplied with screenY and lie inside both the selected target and live viewport; otherwise the target center is used.",
                nullable: true);
            properties["screenY"] = ToolSchema.Number(
                "Optional exact screen-space Y coordinate returned by a trusted app inspection tool. Must be supplied with screenX and lie inside both the selected target and live viewport; otherwise the target center is used.",
                nullable: true);
            properties["targetX"] = ToolSchema.Number(
                "Optional target-local X coordinate returned by an inspection tool for the selected native surface. Must be supplied with targetY and is translated through the selected target's live bounds.",
                nullable: true);
            properties["targetY"] = ToolSchema.Number(
                "Optional target-local Y coordinate returned by an inspection tool for the selected native surface. Must be supplied with targetX and is translated through the selected target's live bounds.",
                nullable: true);
        }

        if (actionKind is LiveUiActionKind.Swipe or LiveUiActionKind.Scroll)
        {
            if (actionKind == LiveUiActionKind.Swipe)
            {
                properties["startNormalizedX"] = ToolSchema.Number(
                    "Optional exact recorded start X as a viewport-normalized coordinate from 0 through 1. Supply all four normalized path coordinates together.",
                    nullable: true);
                properties["startNormalizedY"] = ToolSchema.Number(
                    "Optional exact recorded start Y as a viewport-normalized coordinate from 0 through 1. Supply all four normalized path coordinates together.",
                    nullable: true);
                properties["endNormalizedX"] = ToolSchema.Number(
                    "Optional exact recorded end X as a viewport-normalized coordinate from 0 through 1. Supply all four normalized path coordinates together.",
                    nullable: true);
                properties["endNormalizedY"] = ToolSchema.Number(
                    "Optional exact recorded end Y as a viewport-normalized coordinate from 0 through 1. Supply all four normalized path coordinates together.",
                    nullable: true);
            }

            properties["orientation"] = ToolSchema.String(
                actionKind == LiveUiActionKind.Scroll
                    ? "Direction toward the content to reveal, opposite to finger travel. Down or S reveals content below using an upward finger swipe; up or N reveals content above using a downward finger swipe. Accepts N, NE, E, SE, S, SW, W, NW, cardinal words, or paths such as E to W, NE to SW, and S to N. Defaults to up."
                    : "Finger travel orientation. Accepts N, NE, E, SE, S, SW, W, NW, cardinal words, or paths such as E to W, NE to SW, and S to N. Defaults to up.",
                nullable: true);
            properties["direction"] = ToolSchema.String(
                actionKind == LiveUiActionKind.Scroll
                    ? "Legacy direction toward the content to reveal: down reveals content below and up reveals content above, opposite to finger travel. Use orientation for compass paths."
                    : "Legacy finger direction: up, down, left, or right. Use orientation for compass paths.",
                enumValues: ["up", "down", "left", "right"],
                nullable: true);
            properties["length"] = ToolSchema.Number("Gesture length as a normalized viewport fraction from 0.05 through 0.9. Defaults to 0.4.", nullable: true);
            properties["distance"] = ToolSchema.Number("Legacy alias for length. Normalized viewport fraction from 0.05 through 0.9.", nullable: true);
            properties["durationMs"] = ToolSchema.Integer("Gesture duration from 50 through 2000 milliseconds. Defaults to 250.", nullable: true);
        }

        if (actionKind == LiveUiActionKind.Pinch)
        {
            properties["scale"] = ToolSchema.Number("Final contact separation divided by starting separation. Values below 1 pinch inward; values above 1 spread outward. Accepted range is 0.1 through 4.");
            properties["centerNormalizedX"] = ToolSchema.Number("Optional viewport-normalized pinch center X from 0 through 1. Supply with centerNormalizedY; defaults to the target center.", nullable: true);
            properties["centerNormalizedY"] = ToolSchema.Number("Optional viewport-normalized pinch center Y from 0 through 1. Supply with centerNormalizedX; defaults to the target center.", nullable: true);
            properties["startDistance"] = ToolSchema.Number("Starting distance between contacts as a normalized viewport fraction from 0.05 through 0.8. Defaults to 0.25.", nullable: true);
            properties["angleDegrees"] = ToolSchema.Number("Clockwise contact-axis angle in degrees. Defaults to 0 for a horizontal pinch.", nullable: true);
            properties["durationMs"] = ToolSchema.Integer("Gesture duration from 50 through 2000 milliseconds. Defaults to 300.", nullable: true);
        }

        return properties;
    }

    private string Capability => actionKind switch
    {
        LiveUiActionKind.Tap => "ui.tap",
        LiveUiActionKind.TypeText => "ui.type_text",
        LiveUiActionKind.Swipe => "ui.swipe",
        LiveUiActionKind.Scroll => "ui.scroll",
        LiveUiActionKind.Pinch => "ui.pinch",
        LiveUiActionKind.Back => "ui.back",
        LiveUiActionKind.KeyboardOpen => "keyboard.open",
        LiveUiActionKind.KeyboardDismiss => "keyboard.dismiss",
        _ => throw new ArgumentOutOfRangeException()
    };

    private static double NormalizeX(double x, LiveUiBounds viewport)
        => Math.Clamp((x - viewport.X) / viewport.Width, 0, 1);

    private static double NormalizeY(double y, LiveUiBounds viewport)
        => Math.Clamp((y - viewport.Y) / viewport.Height, 0, 1);

    internal static bool TryResolveTapPoint(
        JsonObject? arguments,
        LiveUiBounds targetBounds,
        LiveUiBounds viewport,
        out LiveUiScreenPoint point,
        out string error)
    {
        var hasScreenX = LiveUiToolSchemas.TryReadDouble(arguments, "screenX", out var screenX);
        var hasScreenY = LiveUiToolSchemas.TryReadDouble(arguments, "screenY", out var screenY);
        var hasTargetX = LiveUiToolSchemas.TryReadDouble(arguments, "targetX", out var targetX);
        var hasTargetY = LiveUiToolSchemas.TryReadDouble(arguments, "targetY", out var targetY);
        var hasNormalizedX = LiveUiToolSchemas.TryReadDouble(arguments, "normalizedX", out var normalizedX);
        var hasNormalizedY = LiveUiToolSchemas.TryReadDouble(arguments, "normalizedY", out var normalizedY);
        if (hasScreenX != hasScreenY)
        {
            point = default;
            error = "screenX and screenY must be supplied together.";
            return false;
        }

        if (hasTargetX != hasTargetY)
        {
            point = default;
            error = "targetX and targetY must be supplied together.";
            return false;
        }

        if (hasNormalizedX != hasNormalizedY)
        {
            point = default;
            error = "normalizedX and normalizedY must be supplied together.";
            return false;
        }

        var coordinateSpaceCount = (hasScreenX ? 1 : 0)
                                   + (hasTargetX ? 1 : 0)
                                   + (hasNormalizedX ? 1 : 0);
        if (coordinateSpaceCount > 1)
        {
            point = default;
            error = "Use either normalizedX/normalizedY, screenX/screenY, or targetX/targetY, not multiple coordinate spaces.";
            return false;
        }

        if (coordinateSpaceCount == 0)
        {
            point = new LiveUiScreenPoint(targetBounds.CenterX, targetBounds.CenterY);
            error = string.Empty;
            return true;
        }

        if (hasTargetX)
        {
            if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                point = default;
                error = "targetX and targetY must be finite numbers.";
                return false;
            }

            screenX = targetBounds.X + targetX;
            screenY = targetBounds.Y + targetY;
        }

        if (hasNormalizedX)
        {
            if (!double.IsFinite(normalizedX)
                || !double.IsFinite(normalizedY)
                || normalizedX < 0
                || normalizedX > 1
                || normalizedY < 0
                || normalizedY > 1)
            {
                point = default;
                error = "normalizedX and normalizedY must be finite numbers from 0 through 1.";
                return false;
            }

            screenX = viewport.X + (normalizedX * viewport.Width);
            screenY = viewport.Y + (normalizedY * viewport.Height);
        }

        if (!double.IsFinite(screenX) || !double.IsFinite(screenY))
        {
            point = default;
            error = "screenX and screenY must be finite numbers.";
            return false;
        }

        var minimumX = Math.Max(targetBounds.X, viewport.X);
        var maximumX = Math.Min(targetBounds.X + targetBounds.Width, viewport.X + viewport.Width);
        var minimumY = Math.Max(targetBounds.Y, viewport.Y);
        var maximumY = Math.Min(targetBounds.Y + targetBounds.Height, viewport.Y + viewport.Height);
        if (screenX < minimumX || screenX > maximumX || screenY < minimumY || screenY > maximumY)
        {
            point = default;
            error = "The requested screen point is outside the visible intersection of the selected target and live viewport.";
            return false;
        }

        point = new LiveUiScreenPoint(screenX, screenY);
        error = string.Empty;
        return true;
    }

    internal static bool TryResolveSwipePath(
        JsonObject? arguments,
        LiveUiBounds gestureBounds,
        LiveUiBounds viewport,
        bool reverseOrientation,
        out LiveUiSwipePath path,
        out string error)
    {
        var exactCoordinateNames = new[]
        {
            "startNormalizedX",
            "startNormalizedY",
            "endNormalizedX",
            "endNormalizedY"
        };
        var suppliedExactCoordinateCount = exactCoordinateNames.Count(name =>
            arguments?[name] is not null);
        var durationMilliseconds = Math.Clamp(
            LiveUiToolSchemas.ReadInteger(arguments, "durationMs", 250),
            50,
            2_000);
        if (suppliedExactCoordinateCount > 0)
        {
            if (suppliedExactCoordinateCount != exactCoordinateNames.Length)
            {
                path = default;
                error = "startNormalizedX, startNormalizedY, endNormalizedX, and endNormalizedY must be supplied together.";
                return false;
            }

            if (!LiveUiToolSchemas.TryReadDouble(arguments, "startNormalizedX", out var recordedStartX)
                || !LiveUiToolSchemas.TryReadDouble(arguments, "startNormalizedY", out var recordedStartY)
                || !LiveUiToolSchemas.TryReadDouble(arguments, "endNormalizedX", out var recordedEndX)
                || !LiveUiToolSchemas.TryReadDouble(arguments, "endNormalizedY", out var recordedEndY)
                || !IsNormalizedCoordinate(recordedStartX)
                || !IsNormalizedCoordinate(recordedStartY)
                || !IsNormalizedCoordinate(recordedEndX)
                || !IsNormalizedCoordinate(recordedEndY))
            {
                path = default;
                error = "Recorded swipe coordinates must be finite numbers from 0 through 1.";
                return false;
            }

            path = new LiveUiSwipePath(
                recordedStartX,
                recordedStartY,
                recordedEndX,
                recordedEndY,
                durationMilliseconds);
            error = string.Empty;
            return true;
        }

        var orientation = LiveUiToolSchemas.ReadString(arguments, "orientation")
                          ?? LiveUiToolSchemas.ReadString(arguments, "direction")
                          ?? "up";
        if (!LiveUiSwipeOrientation.TryParse(orientation, out var vector))
        {
            path = default;
            error = "orientation must be a compass direction or path such as N, NE, E to W, NE to SW, S to N, up, down, left, or right.";
            return false;
        }

        var legacyDistance = LiveUiToolSchemas.ReadDouble(arguments, "distance", 0.4);
        var length = Math.Clamp(
            LiveUiToolSchemas.ReadDouble(arguments, "length", legacyDistance),
            0.05,
            0.9);
        var directionMultiplier = reverseOrientation ? -1 : 1;
        var deltaX = vector.X * length * directionMultiplier;
        var deltaY = vector.Y * length * directionMultiplier;
        var centerX = NormalizeX(gestureBounds.CenterX, viewport);
        var centerY = NormalizeY(gestureBounds.CenterY, viewport);
        var startX = centerX - (deltaX / 2);
        var startY = centerY - (deltaY / 2);
        var endX = centerX + (deltaX / 2);
        var endY = centerY + (deltaY / 2);
        var minimumX = Math.Clamp(NormalizeX(gestureBounds.X, viewport), 0.02, 0.98);
        var maximumX = Math.Clamp(
            NormalizeX(gestureBounds.X + gestureBounds.Width, viewport),
            minimumX,
            0.98);
        var minimumY = Math.Clamp(NormalizeY(gestureBounds.Y, viewport), 0.02, 0.98);
        var maximumY = Math.Clamp(
            NormalizeY(gestureBounds.Y + gestureBounds.Height, viewport),
            minimumY,
            0.98);
        FitSegment(ref startX, ref endX, minimumX, maximumX);
        FitSegment(ref startY, ref endY, minimumY, maximumY);
        path = new LiveUiSwipePath(
            startX,
            startY,
            endX,
            endY,
            durationMilliseconds);
        error = string.Empty;
        return true;
    }

    internal static bool TryResolvePinchPath(
        JsonObject? arguments,
        LiveUiBounds gestureBounds,
        LiveUiBounds viewport,
        out LiveUiPinchPath path,
        out string error)
    {
        if (!LiveUiToolSchemas.TryReadDouble(arguments, "scale", out var scale)
            || !double.IsFinite(scale)
            || scale < 0.1
            || scale > 4)
        {
            path = default;
            error = "scale is required and must be a finite number from 0.1 through 4.";
            return false;
        }

        var hasCenterX = LiveUiToolSchemas.TryReadDouble(arguments, "centerNormalizedX", out var centerX);
        var hasCenterY = LiveUiToolSchemas.TryReadDouble(arguments, "centerNormalizedY", out var centerY);
        if (hasCenterX != hasCenterY)
        {
            path = default;
            error = "centerNormalizedX and centerNormalizedY must be supplied together.";
            return false;
        }

        if (!hasCenterX)
        {
            centerX = NormalizeX(gestureBounds.CenterX, viewport);
            centerY = NormalizeY(gestureBounds.CenterY, viewport);
        }

        var startDistance = LiveUiToolSchemas.ReadDouble(arguments, "startDistance", 0.2);
        var angleDegrees = LiveUiToolSchemas.ReadDouble(arguments, "angleDegrees", 0);
        var durationMilliseconds = Math.Clamp(
            LiveUiToolSchemas.ReadInteger(arguments, "durationMs", 300),
            50,
            2_000);
        var endDistance = startDistance * scale;
        if (!IsNormalizedCoordinate(centerX)
            || !IsNormalizedCoordinate(centerY)
            || !double.IsFinite(startDistance)
            || startDistance < 0.05
            || startDistance > 0.8
            || !double.IsFinite(endDistance)
            || endDistance < 0.02
            || endDistance > 0.9
            || !double.IsFinite(angleDegrees))
        {
            path = default;
            error = "Pinch center, startDistance, scale, and angle must produce finite in-viewport contacts; startDistance accepts 0.05 through 0.8 and final distance accepts 0.02 through 0.9.";
            return false;
        }

        var radians = angleDegrees * (Math.PI / 180);
        var directionX = Math.Cos(radians);
        var directionY = Math.Sin(radians);
        var startRadius = startDistance / 2;
        var endRadius = endDistance / 2;
        path = new LiveUiPinchPath(
            centerX - (directionX * startRadius),
            centerY - (directionY * startRadius),
            centerX + (directionX * startRadius),
            centerY + (directionY * startRadius),
            centerX - (directionX * endRadius),
            centerY - (directionY * endRadius),
            centerX + (directionX * endRadius),
            centerY + (directionY * endRadius),
            durationMilliseconds);

        var minimumX = Math.Clamp(NormalizeX(gestureBounds.X, viewport), 0, 1);
        var maximumX = Math.Clamp(NormalizeX(gestureBounds.X + gestureBounds.Width, viewport), 0, 1);
        var minimumY = Math.Clamp(NormalizeY(gestureBounds.Y, viewport), 0, 1);
        var maximumY = Math.Clamp(NormalizeY(gestureBounds.Y + gestureBounds.Height, viewport), 0, 1);
        if (!ContainsPinchPath(path, minimumX, maximumX, minimumY, maximumY))
        {
            path = default;
            error = "The requested pinch contacts extend outside the selected target or live viewport. Move the center inward or reduce startDistance/scale.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ContainsPinchPath(
        LiveUiPinchPath path,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY)
        => ContainsPoint(path.PrimaryStartX, path.PrimaryStartY, minimumX, maximumX, minimumY, maximumY)
           && ContainsPoint(path.SecondaryStartX, path.SecondaryStartY, minimumX, maximumX, minimumY, maximumY)
           && ContainsPoint(path.PrimaryEndX, path.PrimaryEndY, minimumX, maximumX, minimumY, maximumY)
           && ContainsPoint(path.SecondaryEndX, path.SecondaryEndY, minimumX, maximumX, minimumY, maximumY);

    private static bool ContainsPoint(
        double x,
        double y,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY)
        => x >= minimumX && x <= maximumX && y >= minimumY && y <= maximumY;

    private static bool IsNormalizedCoordinate(double value)
        => double.IsFinite(value) && value >= 0 && value <= 1;

    private static void FitSegment(ref double start, ref double end, double minimum, double maximum)
    {
        if (start < minimum)
        {
            end += minimum - start;
            start = minimum;
        }
        else if (start > maximum)
        {
            end -= start - maximum;
            start = maximum;
        }

        if (end < minimum)
        {
            start += minimum - end;
            end = minimum;
        }
        else if (end > maximum)
        {
            start -= end - maximum;
            end = maximum;
        }

        start = Math.Clamp(start, minimum, maximum);
        end = Math.Clamp(end, minimum, maximum);
    }

    private void RecordActionLog(
        string sessionId,
        string actionId,
        string message,
        LogPriority priority)
    {
        runtimeState.AddSessionLog(
            sessionId,
            new LogEntry(DateTimeOffset.UtcNow, message)
            {
                Source = "Ansight UI Automation",
                Tag = Capability,
                EventId = actionId,
                Priority = priority
            });
    }
}
