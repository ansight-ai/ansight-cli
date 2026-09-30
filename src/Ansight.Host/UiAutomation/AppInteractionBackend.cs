using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.DeviceLifecycle;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;

namespace Ansight.Host.UiAutomation;

internal sealed class AppInteractionBackend : IAppInteractionBackend
{
    private readonly IExternalSessionScreenshotCaptureManager? externalScreenshots;
    private readonly IRuntimeState runtimeState;
    private readonly IApplicationPaths applicationPaths;
    private readonly IAppToolBridge appToolBridge;
    private readonly UiInputRouter input;
    private readonly AppSessionSnapshot session;
    private readonly string deviceIdentifier;
    private readonly IOperationDispatcher? taskDispatcher;
    private readonly string? repositoryRootPath;
    private bool prepared;
    private AppInteractionVisualSample? previousSample;
    private readonly AppInteractionTree tree;

    public AppInteractionBackend(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IAppToolBridge appToolBridge,
        UiInputRouter input,
        string sessionId,
        IOperationDispatcher? taskDispatcher = null,
        string? repositoryRootPath = null,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null)
    {
        this.externalScreenshots = externalScreenshots;
        this.runtimeState = runtimeState;
        this.applicationPaths = applicationPaths;
        this.appToolBridge = appToolBridge;
        this.input = input;
        this.taskDispatcher = taskDispatcher;
        this.repositoryRootPath = repositoryRootPath is null ? null : Path.GetFullPath(repositoryRootPath);
        if (!new SessionResolver(runtimeState, appToolBridge).TryResolveLiveSession(
                new System.Text.Json.Nodes.JsonObject { ["sessionId"] = sessionId }, out var snapshot, out var error))
            throw new InvalidOperationException(error);
        session = snapshot!;
        tree = new AppInteractionTree(runtimeState, appToolBridge, input, session);
        deviceIdentifier = input.ResolveDeviceIdentifier(sessionId, DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session))
            ?? throw new InvalidOperationException("The session does not report a native device identifier; input cannot be safely targeted.");
    }

    public string SessionId => session.SessionId;
    public AppInteractionUi? Ui => tree.Latest;

    public void EnsureConnected()
    {
        if (!appToolBridge.IsSessionConnected(SessionId) && !runtimeState.IsDeviceSessionActive(SessionId))
            throw new InvalidOperationException($"Session '{SessionId}' disconnected. Reconnect explicitly; an interaction context never switches apps or sessions.");
        if (!string.Equals(input.ResolveDeviceIdentifier(SessionId, deviceIdentifier), deviceIdentifier, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The session's input target changed. Reconnect the interactive bridge explicitly.");
    }

    public async Task<AppInteractionScreenshot?> CaptureAsync(
        string actionId, CancellationToken cancellationToken, bool afterInput = false)
    {
        EnsureConnected();
        if (!prepared)
        {
            var preflight = await input.PrepareForInputAsync(
                new UiInputPreflightRequest(SessionId, deviceIdentifier, session.AppId), cancellationToken).ConfigureAwait(false);
            if (!preflight.IsSuccess)
                throw new InvalidOperationException(preflight.Message);
            prepared = true;
        }

        var settling = new AppInteractionVisualSettling(afterInput ? previousSample : null);
        previousSample = null;
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await CaptureFrameAsync(256).ConfigureAwait(false);
            if (probe is null) return null;
            settling.Observe(AppInteractionVisualSample.FromPng(probe.Bytes), timer.Elapsed.TotalMilliseconds);
            if (settling.IsQuiet(timer.Elapsed.TotalMilliseconds)
                || timer.Elapsed.TotalMilliseconds >= AppInteractionVisualSettling.MaximumMilliseconds)
            {
                var final = await CaptureFrameAsync(1_024).ConfigureAwait(false);
                if (final is null) return null;
                // Bracket the full frame with matching low-resolution observations.
                // Comparing differently resized PNGs directly confuses rasterization
                // differences with animation, particularly around small text.
                var verification = await CaptureFrameAsync(256).ConfigureAwait(false);
                if (verification is null) return null;
                var sample = AppInteractionVisualSample.FromPng(verification.Bytes);
                settling.Observe(sample, timer.Elapsed.TotalMilliseconds);
                if (settling.IsQuiet(timer.Elapsed.TotalMilliseconds)
                    || timer.Elapsed.TotalMilliseconds >= AppInteractionVisualSettling.MaximumMilliseconds)
                {
                    var screenshot = await LiveUiActionEvidence.PersistAppScreenshotAsync(
                        runtimeState, session, final, applicationPaths).ConfigureAwait(false);
                    if (screenshot is null) return null;
                    previousSample = sample;
                    var description = settling.Describe(timer.Elapsed.TotalMilliseconds);
                    await tree.CaptureAsync(actionId, null, cancellationToken, screenshot).ConfigureAwait(false);
                    return new AppInteractionScreenshot(screenshot.Frame.FrameId, screenshot.ArtifactPath,
                        screenshot.Frame.CapturedAtUtc, screenshot.Frame.Width, screenshot.Frame.Height,
                        description);
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        async Task<AppScreenshot?> CaptureFrameAsync(int width)
        {
            if (session.CaptureSource != WorkspaceExecutionModes.Device)
                return await LiveUiActionEvidence.ReadAppScreenshotAsync(appToolBridge, session, actionId, "after",
                    actionId, cancellationToken, afterScreenUpdates: true, maxWidth: width).ConfigureAwait(false);
            return await LiveUiActionEvidence.ReadExternalScreenshotAsync(
                externalScreenshots, session, width, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<UiInputResult> ExecuteAsync(AppInteractionRequest request, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (request.Target is not null) return ExecuteTargetedAsync(request, cancellationToken);
        return request.Command switch
        {
            "tap" => input.TapAsync(new UiTapRequest(SessionId, deviceIdentifier,
                request.X!.Value, request.Y!.Value, session.AppId), cancellationToken),
            "swipe" => input.SwipeAsync(new UiSwipeRequest(SessionId, deviceIdentifier,
                request.X!.Value, request.Y!.Value, request.EndX!.Value, request.EndY!.Value,
                request.DurationMs ?? 250, session.AppId), cancellationToken),
            "pinch" => input.PinchAsync(CreatePinch(request), cancellationToken),
            "type" => input.TypeTextAsync(new UiTextRequest(SessionId, deviceIdentifier,
                request.Value!, ReplaceExisting: false, session.AppId), cancellationToken),
            "back" => input.PressButtonAsync(new UiButtonRequest(SessionId, deviceIdentifier, "back", session.AppId), cancellationToken),
            _ => throw new ArgumentException($"Unsupported input command '{request.Command}'.")
        };
    }

    private async Task<UiInputResult> ExecuteTargetedAsync(AppInteractionRequest request, CancellationToken cancellationToken)
    {
        var capture = await tree.CaptureAsync($"interaction-{Guid.NewGuid():N}", request.Target, cancellationToken).ConfigureAwait(false);
        if (capture is null) return UiInputResult.Failure("No fresh UI tree is available; no input was sent.");
        if (capture.Viewport is not { Width: > 0, Height: > 0 } viewport)
            return UiInputResult.Failure("The UI tree has no usable viewport; no input was sent.");
        var matches = LiveUiNodeQuery.Find(capture.Root, AppInteractionTree.Selector(request.Target!), capture.TypeRegistry);
        if (matches.Count != 1)
            return UiInputResult.Failure($"The target matched {matches.Count} visible, enabled nodes. Use an exact unique target; no input was sent.");
        var target = matches[0];
        var bounds = LiveUiNodeQuery.ReadBounds(target.Node);
        if (bounds is null || LiveUiBounds.Intersection(bounds, viewport) is not { Width: > 0, Height: > 0 } visibleBounds
            || LiveUiActionTool.IsUnsafeImplicitTapTarget(target, bounds, viewport))
            return UiInputResult.Failure("The target is offscreen or an unsafe non-actionable container; no input was sent.");
        if (request.Command == "type"
            && LiveUiNodeQuery.ReadRole(target.Node, capture.TypeRegistry) != "textbox"
            && !LiveUiNodeQuery.ReadSupportedActions(target.Node, capture.TypeRegistry).Contains("typeText"))
            return UiInputResult.Failure("The target is not an editable text input; no input was sent.");
        var focus = await input.TapAsync(new UiTapRequest(SessionId, deviceIdentifier,
            (visibleBounds.CenterX - viewport.X) / viewport.Width,
            (visibleBounds.CenterY - viewport.Y) / viewport.Height, session.AppId), cancellationToken).ConfigureAwait(false);
        if (request.Command == "tap" || !focus.IsSuccess) return focus;
        return await input.TypeTextAsync(new UiTextRequest(SessionId, deviceIdentifier,
            request.Value!, request.ReplaceExisting ?? true, session.AppId), cancellationToken).ConfigureAwait(false);
    }

    private UiPinchRequest CreatePinch(AppInteractionRequest request)
    {
        var x = request.X!.Value;
        var y = request.Y!.Value;
        var scale = request.Scale!.Value;
        var room = Math.Min(x, 1 - x);
        if (room < 0.01)
            throw new ArgumentException("Pinch center must leave room for both fingers (x between 0.01 and 0.99).");
        var start = Math.Min(0.1, room / Math.Max(1, scale));
        var end = start * scale;
        return new UiPinchRequest(SessionId, deviceIdentifier,
            x - start, y, x + start, y, x - end, y, x + end, y,
            request.DurationMs ?? 250, session.AppId);
    }

    public RepositoryTaskCatalog ListTasks()
    {
        EnsureTaskRepository();
        return taskDispatcher!.InspectRepositoryTasks(repositoryRootPath!, session.AppId);
    }

    public Task<RepositoryTaskRunResult> RunTaskAsync(string taskId, JsonObject? suppliedInput, CancellationToken cancellationToken)
    {
        EnsureTaskRepository();
        return taskDispatcher!.RunRepositoryTaskAsync(
            repositoryRootPath!, session.AppId, SessionId, taskId, suppliedInput, cancellationToken);
    }

    private void EnsureTaskRepository()
    {
        EnsureConnected();
        if (taskDispatcher is null || repositoryRootPath is null)
            throw new ArgumentException("Task commands require --repository <root> when opening the interactive connection.");
    }

    public void Record(AppInteractionResult result)
    {
        // Do not duplicate task inputs, outputs or messages in interaction events.
        // The task engine retains its own run result; frames belong to the session store.
        var recorded = result.Command == "task"
            ? result with { Task = null, Message = result.Task is { } task
                ? $"Task '{task.TaskId}' run '{task.RunId}': {task.Status}."
                : "Task did not produce a run result." }
            : result;
        // Full trees are already retained; avoid duplicating potentially sensitive text in events.
        recorded = recorded with { Ui = null };
        runtimeState.AddSessionApplicationEvents(SessionId,
        [
            new SessionApplicationEvent($"interaction-{Guid.NewGuid():N}", $"Interact: {result.Command}",
                "ansight.interaction", JsonSerializer.Serialize(recorded), DateTimeOffset.UtcNow, 0)
        ]);
    }
}
