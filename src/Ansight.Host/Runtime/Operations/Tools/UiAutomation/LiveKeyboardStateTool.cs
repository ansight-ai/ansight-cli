using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveKeyboardState
{
    private const string KeyboardVisiblePropertyName = "keyboardVisible";

    public static bool TryRead(LiveUiTreeCapture? capture, out bool isOpen)
    {
        if (capture is null)
        {
            isOpen = false;
            return false;
        }

        return TryRead(capture.ToolId, capture.Payload, out isOpen);
    }

    internal static bool TryRead(string toolId, JsonObject payload, out bool isOpen)
    {
        if (!string.Equals(toolId, LiveUiTreeCapture.DeviceAccessibilityToolId, StringComparison.Ordinal)
            || payload[KeyboardVisiblePropertyName] is not JsonValue value
            || !value.TryGetValue<bool>(out isOpen))
        {
            isOpen = false;
            return false;
        }

        return true;
    }
}

internal sealed class LiveKeyboardStateTool : RemoteAppOperation
{
    public LiveKeyboardStateTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_is_keyboard_open";

    protected override string Title => "Check Whether The Keyboard Is Open";

    protected override string Description =>
        "Determine whether the system software keyboard is currently visible using fresh device accessibility evidence.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching live session exists.", nullable: true),
            ["deviceId"] = ToolSchema.String("Native device identifier used to select a matching live app session. May be combined with appId.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        var captureResult = await LiveUiTreeCapture.CaptureDeviceAccessibilityAsync(
            snapshot!,
            uiInputRouter,
            cancellationToken: ToolExecutionCancellation.Current,
            allowCached: false).ConfigureAwait(false);
        if (!captureResult.IsSuccess || captureResult.Capture is null)
        {
            return ToolError(
                $"Keyboard visibility is unavailable because fresh device accessibility could not be captured: {captureResult.Message}");
        }

        var capture = captureResult.Capture;
        if (!LiveKeyboardState.TryRead(capture, out var isOpen))
        {
            return ToolError("The device accessibility provider did not report keyboard visibility.");
        }

        var persisted = SessionVisualTreePersistence.Persist(
            runtimeState,
            capture.Session,
            capture.ToolId,
            capture.RawPayload);
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["capability"] = "keyboard.is_open",
                ["sessionId"] = capture.Session.SessionId,
                ["appId"] = capture.Session.AppId,
                ["isOpen"] = isOpen,
                ["capturedAtUtc"] = capture.CapturedAtUtc,
                ["evidenceSource"] = "deviceAccessibility",
                ["visualTreeToolId"] = capture.ToolId,
                ["visualTreeSnapshotId"] = persisted.Snapshot?.SnapshotId,
                ["message"] = isOpen
                    ? "The system software keyboard is open."
                    : "The system software keyboard is closed."
            },
            isError: false);
    }
}
