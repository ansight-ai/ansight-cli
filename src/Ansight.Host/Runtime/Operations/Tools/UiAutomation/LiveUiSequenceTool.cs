using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal enum LiveUiSequenceActionKind
{
    Tap,
    Swipe,
    Pinch,
    Back
}

internal sealed record LiveUiSequenceAction(
    LiveUiSequenceActionKind Kind,
    LiveUiScreenPoint? TapPoint,
    LiveUiSwipePath? SwipePath,
    LiveUiPinchPath? PinchPath,
    int PauseAfterMilliseconds);

internal sealed class LiveUiSequenceTool : RemoteAppOperation
{
    private const int MaximumActions = 32;

    public LiveUiSequenceTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_run_ui_sequence";

    protected override string Title => "Run Live UI Sequence";

    protected override string Description =>
        "Run up to 32 recorded tap, swipe, pinch, or back actions in one host round trip, with one persisted evidence checkpoint before and after the sequence and an inline final screenshot.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["actions"] = ToolSchema.Array(
                ToolSchema.Object(
                    properties: new Dictionary<string, ToolSchema>
                    {
                        ["kind"] = ToolSchema.String("Action kind.", enumValues: ["tap", "swipe", "pinch", "back"]),
                        ["normalizedX"] = ToolSchema.Number("Tap X as a viewport-normalized coordinate from 0 through 1.", nullable: true),
                        ["normalizedY"] = ToolSchema.Number("Tap Y as a viewport-normalized coordinate from 0 through 1.", nullable: true),
                        ["startNormalizedX"] = ToolSchema.Number("Swipe start X from 0 through 1.", nullable: true),
                        ["startNormalizedY"] = ToolSchema.Number("Swipe start Y from 0 through 1.", nullable: true),
                        ["endNormalizedX"] = ToolSchema.Number("Swipe end X from 0 through 1.", nullable: true),
                        ["endNormalizedY"] = ToolSchema.Number("Swipe end Y from 0 through 1.", nullable: true),
                        ["orientation"] = ToolSchema.String("Swipe direction or compass path, for example up, W, or NE to SW.", nullable: true),
                        ["direction"] = ToolSchema.String("Legacy swipe direction.", enumValues: ["up", "down", "left", "right"], nullable: true),
                        ["length"] = ToolSchema.Number("Swipe length as a normalized viewport fraction.", nullable: true),
                        ["distance"] = ToolSchema.Number("Legacy alias for swipe length.", nullable: true),
                        ["scale"] = ToolSchema.Number("Pinch final contact separation divided by starting separation.", nullable: true),
                        ["centerNormalizedX"] = ToolSchema.Number("Pinch center X from 0 through 1.", nullable: true),
                        ["centerNormalizedY"] = ToolSchema.Number("Pinch center Y from 0 through 1.", nullable: true),
                        ["startDistance"] = ToolSchema.Number("Starting pinch contact separation.", nullable: true),
                        ["angleDegrees"] = ToolSchema.Number("Clockwise pinch-axis angle in degrees.", nullable: true),
                        ["durationMs"] = ToolSchema.Integer("Swipe or pinch duration from 50 through 2000 milliseconds.", nullable: true),
                        ["pauseAfterMs"] = ToolSchema.Integer("Pause after this action from 0 through 5000 milliseconds. Defaults to 0.", nullable: true)
                    },
                    required: ["kind"],
                    additionalProperties: false),
                description: "Ordered recorded actions. Accepted count is 1 through 32.",
                nullable: false),
            ["continueOnError"] = ToolSchema.Boolean("Continue with later actions after an input failure. Defaults to false.", nullable: true),
            ["settleMs"] = ToolSchema.Integer("Delay before final evidence capture from 0 through 2000 milliseconds. Defaults to 200.", nullable: true),
            ["includeScreenshot"] = ToolSchema.Boolean("Include the final screenshot as inline image content. Defaults to true.", nullable: true)
        },
        required: ["actions"],
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
            CancellationToken.None);
        if (!preflightResult.IsSuccess)
        {
            return ToolError(preflightResult.Message);
        }

        var captureResult = await LiveUiTreeCapture.CaptureAsync(
            snapshot!,
            appToolBridge,
            uiInputRouter,
            Name,
            correlationId,
            CancellationToken.None);
        if (!captureResult.IsSuccess || captureResult.Capture is null)
        {
            return ToolError(captureResult.Message);
        }

        var capture = captureResult.Capture;
        var viewport = capture.Viewport;
        if (viewport is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return ToolError("The visual tree does not expose usable screen-space bounds for host input.");
        }

        if (!TryParseActions(arguments?["actions"] as JsonArray, viewport, out var actions, out var parseError))
        {
            return ToolError(parseError);
        }

        var sequenceId = $"ui-sequence-{Guid.NewGuid():N}";
        var includeScreenshot = arguments?["includeScreenshot"]?.GetValue<bool>() ?? true;
        var beforeEvidence = await LiveUiActionEvidence.CaptureAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            capture,
            sequenceId,
            "ui.sequence",
            "before",
            correlationId,
            CancellationToken.None,
            captureScreenshot: includeScreenshot, externalScreenshots: externalScreenshots);
        var continueOnError = arguments?["continueOnError"]?.GetValue<bool>() ?? false;
        var settleMilliseconds = Math.Clamp(
            LiveUiToolSchemas.ReadInteger(arguments, "settleMs", 200),
            0,
            2_000);
        var actionResults = new JsonArray();
        var failureCount = 0;
        var completedCount = 0;
        int? stoppedAtIndex = null;
        string? finalBackend = null;
        RecordSequenceLog(
            capture.Session.SessionId,
            sequenceId,
            $"Starting {actions.Count} UI actions in one sequence.",
            LogPriority.Information);

        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var result = await ExecuteActionAsync(
                capture,
                deviceIdentifier,
                action,
                CancellationToken.None);
            completedCount++;
            finalBackend = result.Backend;
            if (!result.IsSuccess)
            {
                failureCount++;
            }

            actionResults.Add(new JsonObject
            {
                ["index"] = index,
                ["kind"] = ToWireName(action.Kind),
                ["performed"] = result.IsSuccess,
                ["inputBackend"] = result.Backend,
                ["message"] = result.Message,
                ["pauseAfterMs"] = action.PauseAfterMilliseconds
            });
            RecordSequenceLog(
                capture.Session.SessionId,
                sequenceId,
                result.IsSuccess
                    ? $"Sequence action {index + 1}/{actions.Count} ({ToWireName(action.Kind)}) completed through {result.Backend}."
                    : $"Sequence action {index + 1}/{actions.Count} ({ToWireName(action.Kind)}) failed: {result.Message}",
                result.IsSuccess ? LogPriority.Information : LogPriority.Error);

            if (!result.IsSuccess && !continueOnError)
            {
                stoppedAtIndex = index;
                break;
            }

            if (action.PauseAfterMilliseconds > 0)
            {
                await Task.Delay(action.PauseAfterMilliseconds);
            }
        }

        var afterCapture = await LiveUiEvidenceCapture.CaptureAfterAsync(
            runtimeState,
            applicationPaths,
            appToolBridge,
            uiInputRouter,
            capture,
            sequenceId,
            "ui.sequence",
            Name,
            correlationId,
            settleMilliseconds,
            CancellationToken.None,
            captureScreenshot: includeScreenshot, externalScreenshots: externalScreenshots);
        var performed = failureCount == 0 && completedCount == actions.Count;
        var message = performed
            ? $"Completed {completedCount} UI actions in one host round trip."
            : $"Completed {completedCount} of {actions.Count} UI actions with {failureCount} failure(s).";
        RecordSequenceLog(
            capture.Session.SessionId,
            sequenceId,
            message,
            performed ? LogPriority.Information : LogPriority.Error);

        var structuredContent = new JsonObject
        {
            ["sequenceId"] = sequenceId,
            ["capability"] = "ui.sequence",
            ["performed"] = performed,
            ["sessionId"] = capture.Session.SessionId,
            ["appId"] = capture.Session.AppId,
            ["deviceIdentifier"] = deviceIdentifier,
            ["visualTreeToolId"] = capture.ToolId,
            ["capturedAtUtc"] = capture.CapturedAtUtc,
            ["inputBackend"] = finalBackend ?? availability.Backend,
            ["requestedActionCount"] = actions.Count,
            ["completedActionCount"] = completedCount,
            ["failureCount"] = failureCount,
            ["stoppedAtIndex"] = stoppedAtIndex,
            ["message"] = message,
            ["actions"] = actionResults,
            ["evidence"] = new JsonObject
            {
                ["mode"] = "sequenceEndpoints",
                ["before"] = LiveUiActionEvidence.ToJson(beforeEvidence),
                ["after"] = LiveUiActionEvidence.ToJson(afterCapture.Evidence)
            }
        };
        return LiveUiActionResponse.Build(
            structuredContent,
            afterCapture.Evidence,
            includeScreenshot,
            isError: !performed);
    }

    private async Task<UiInputResult> ExecuteActionAsync(
        LiveUiTreeCapture capture,
        string deviceIdentifier,
        LiveUiSequenceAction action,
        CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case LiveUiSequenceActionKind.Tap:
                var tapPoint = action.TapPoint!.Value;
                return await uiInputRouter.TapAsync(
                    new UiTapRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        tapPoint.X,
                        tapPoint.Y,
                        capture.Session.AppId),
                    cancellationToken);
            case LiveUiSequenceActionKind.Swipe:
                var swipePath = action.SwipePath!.Value;
                return await uiInputRouter.SwipeAsync(
                    new UiSwipeRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        swipePath.StartX,
                        swipePath.StartY,
                        swipePath.EndX,
                        swipePath.EndY,
                        swipePath.DurationMilliseconds,
                        capture.Session.AppId),
                    cancellationToken);
            case LiveUiSequenceActionKind.Pinch:
                var pinchPath = action.PinchPath!.Value;
                return await uiInputRouter.PinchAsync(
                    new UiPinchRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        pinchPath.PrimaryStartX,
                        pinchPath.PrimaryStartY,
                        pinchPath.SecondaryStartX,
                        pinchPath.SecondaryStartY,
                        pinchPath.PrimaryEndX,
                        pinchPath.PrimaryEndY,
                        pinchPath.SecondaryEndX,
                        pinchPath.SecondaryEndY,
                        pinchPath.DurationMilliseconds,
                        capture.Session.AppId),
                    cancellationToken);
            case LiveUiSequenceActionKind.Back:
                return await uiInputRouter.PressButtonAsync(
                    new UiButtonRequest(
                        capture.Session.SessionId,
                        deviceIdentifier,
                        "back",
                        capture.Session.AppId),
                    cancellationToken);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    internal static bool TryParseActions(
        JsonArray? source,
        LiveUiBounds viewport,
        out IReadOnlyList<LiveUiSequenceAction> actions,
        out string error)
    {
        if (source is null || source.Count is < 1 or > MaximumActions)
        {
            actions = [];
            error = $"actions must contain between 1 and {MaximumActions} entries.";
            return false;
        }

        var parsed = new List<LiveUiSequenceAction>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] is not JsonObject action)
            {
                actions = [];
                error = $"actions[{index}] must be an object.";
                return false;
            }

            if (!TryParseAction(action, viewport, out var parsedAction, out var actionError))
            {
                actions = [];
                error = $"actions[{index}]: {actionError}";
                return false;
            }

            parsed.Add(parsedAction!);
        }

        actions = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryParseAction(
        JsonObject action,
        LiveUiBounds viewport,
        out LiveUiSequenceAction? parsed,
        out string error)
    {
        var pauseAfterMilliseconds = Math.Clamp(
            LiveUiToolSchemas.ReadInteger(action, "pauseAfterMs", 0),
            0,
            5_000);
        var kind = LiveUiToolSchemas.ReadString(action, "kind")?.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "tap":
                if (!LiveUiToolSchemas.TryReadDouble(action, "normalizedX", out var tapX)
                    || !LiveUiToolSchemas.TryReadDouble(action, "normalizedY", out var tapY)
                    || !double.IsFinite(tapX)
                    || !double.IsFinite(tapY)
                    || tapX < 0
                    || tapX > 1
                    || tapY < 0
                    || tapY > 1)
                {
                    parsed = null;
                    error = "tap requires finite normalizedX and normalizedY values from 0 through 1.";
                    return false;
                }

                parsed = new LiveUiSequenceAction(
                    LiveUiSequenceActionKind.Tap,
                    new LiveUiScreenPoint(tapX, tapY),
                    null,
                    null,
                    pauseAfterMilliseconds);
                error = string.Empty;
                return true;
            case "swipe":
                if (!LiveUiActionTool.TryResolveSwipePath(
                        action,
                        viewport,
                        viewport,
                        reverseOrientation: false,
                        out var swipePath,
                        out error))
                {
                    parsed = null;
                    return false;
                }

                parsed = new LiveUiSequenceAction(
                    LiveUiSequenceActionKind.Swipe,
                    null,
                    swipePath,
                    null,
                    pauseAfterMilliseconds);
                return true;
            case "pinch":
                if (!LiveUiActionTool.TryResolvePinchPath(
                        action,
                        viewport,
                        viewport,
                        out var pinchPath,
                        out error))
                {
                    parsed = null;
                    return false;
                }

                parsed = new LiveUiSequenceAction(
                    LiveUiSequenceActionKind.Pinch,
                    null,
                    null,
                    pinchPath,
                    pauseAfterMilliseconds);
                return true;
            case "back":
                parsed = new LiveUiSequenceAction(
                    LiveUiSequenceActionKind.Back,
                    null,
                    null,
                    null,
                    pauseAfterMilliseconds);
                error = string.Empty;
                return true;
            default:
                parsed = null;
                error = "kind must be tap, swipe, pinch, or back.";
                return false;
        }
    }

    private void RecordSequenceLog(
        string sessionId,
        string sequenceId,
        string message,
        LogPriority priority)
    {
        runtimeState.AddSessionLog(
            sessionId,
            new LogEntry(DateTimeOffset.UtcNow, message)
            {
                Source = "Ansight UI Automation",
                Tag = "ui.sequence",
                EventId = sequenceId,
                Priority = priority
            });
    }

    private static string ToWireName(LiveUiSequenceActionKind kind)
        => kind switch
        {
            LiveUiSequenceActionKind.Tap => "tap",
            LiveUiSequenceActionKind.Swipe => "swipe",
            LiveUiSequenceActionKind.Pinch => "pinch",
            LiveUiSequenceActionKind.Back => "back",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
