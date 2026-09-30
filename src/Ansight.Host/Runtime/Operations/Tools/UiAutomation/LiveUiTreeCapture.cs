using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class LiveUiTreeCapture
{
    public const string DeviceAccessibilityToolId = "device.accessibility";

    private LiveUiTreeCapture(
        AppSessionSnapshot session,
        string toolId,
        JsonObject rawPayload,
        JsonObject payload,
        JsonObject root,
        VisualTreeTypeRegistry typeRegistry,
        LiveUiBounds? viewport,
        DateTimeOffset capturedAtUtc)
    {
        Session = session;
        ToolId = toolId;
        RawPayload = rawPayload;
        Payload = payload;
        Root = root;
        TypeRegistry = typeRegistry;
        Viewport = viewport;
        CapturedAtUtc = capturedAtUtc;
    }

    public AppSessionSnapshot Session { get; }

    public string ToolId { get; }

    public JsonObject RawPayload { get; }

    public JsonObject Payload { get; }

    public JsonObject Root { get; }

    public VisualTreeTypeRegistry TypeRegistry { get; }

    public LiveUiBounds? Viewport { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public static Task<LiveUiCaptureResult> CaptureAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken)
        => CaptureAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            selector: null,
            cancellationToken);

    public static Task<LiveUiCaptureResult> CaptureAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        UiInputRouter uiInputRouter,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken)
        => CaptureDeviceFirstAsync(
            session,
            appToolBridge,
            uiInputRouter,
            operationName,
            correlationId,
            selector: null,
            cancellationToken);

    public static Task<LiveUiCaptureResult> CaptureForSelectorAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        LiveUiSelector selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return CaptureAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            selector,
            cancellationToken);
    }

    public static Task<LiveUiCaptureResult> CaptureForSelectorAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        UiInputRouter uiInputRouter,
        string operationName,
        string? correlationId,
        LiveUiSelector selector,
        CancellationToken cancellationToken,
        bool allowCached = true)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return CaptureDeviceFirstAsync(
            session,
            appToolBridge,
            uiInputRouter,
            operationName,
            correlationId,
            selector,
            cancellationToken,
            allowCached);
    }

    public static Task<LiveUiCaptureResult> CaptureAccessibilityAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken)
        => CaptureSingleToolAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            preferFrameworkTool: true,
            cancellationToken);

    public static async Task<LiveUiCaptureResult> CaptureAccessibilityAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        UiInputRouter uiInputRouter,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken,
        bool allowCached = true)
    {
        var deviceResult = await CaptureDeviceAccessibilityAsync(
            session,
            uiInputRouter,
            cancellationToken: cancellationToken,
            allowCached: allowCached).ConfigureAwait(false);
        if (deviceResult.IsSuccess)
        {
            return deviceResult;
        }

        var frameworkResult = await CaptureAccessibilityAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            cancellationToken).ConfigureAwait(false);
        return AddAttemptedToolIds(
            frameworkResult,
            [DeviceAccessibilityToolId, .. frameworkResult.AttemptedToolIds]);
    }

    public static async Task<LiveUiCaptureResult> CaptureDeviceAccessibilityAsync(
        AppSessionSnapshot session,
        UiInputRouter uiInputRouter,
        int maxNodes = 256,
        int maxDepth = 32,
        CancellationToken cancellationToken = default,
        bool allowCached = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(uiInputRouter);
        var reportedIdentifier = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session);
        var deviceIdentifier = uiInputRouter.ResolveDeviceIdentifier(
            session.SessionId,
            reportedIdentifier);
        if (deviceIdentifier is null)
        {
            return LiveUiCaptureResult.Failure(
                "The live session does not report device.nativeDeviceId, so device accessibility cannot be targeted.")
                with { AttemptedToolIds = [DeviceAccessibilityToolId] };
        }

        var result = await uiInputRouter.CaptureAccessibilityAsync(
            new UiAccessibilityRequest(
                session.SessionId,
                deviceIdentifier,
                session.AppId,
                Math.Clamp(maxNodes, 1, 2_000),
                Math.Clamp(maxDepth, 1, 64),
                allowCached && session.CaptureSource != WorkspaceExecutionModes.Device),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Payload is null)
        {
            return LiveUiCaptureResult.Failure(result.Message)
                with { AttemptedToolIds = [DeviceAccessibilityToolId] };
        }

        return CreateCapture(
            session,
            DeviceAccessibilityToolId,
            result.Payload,
            result.Message);
    }

    public static Task<LiveUiCaptureResult> CaptureNativeVisualTreeAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        CancellationToken cancellationToken)
        => CaptureSingleToolAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            preferFrameworkTool: false,
            cancellationToken);

    private static async Task<LiveUiCaptureResult> CaptureSingleToolAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        bool preferFrameworkTool,
        CancellationToken cancellationToken)
    {
        if (session.CaptureSource == WorkspaceExecutionModes.Device)
            return LiveUiCaptureResult.Failure("App visual trees require the Ansight SDK. Use device accessibility or screenshot text.");
        var requestContext = new AppToolBridgeRequestContext("host-ui", operationName, correlationId);
        var catalogResponse = await appToolBridge.QueryToolsAsync(
            session.SessionId,
            cancellationToken,
            requestContext);
        if (!catalogResponse.Success || catalogResponse.Envelope is null)
        {
            return LiveUiCaptureResult.Failure(catalogResponse.Message);
        }

        if (string.Equals(catalogResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return LiveUiCaptureResult.Failure(ReadError(catalogResponse.Envelope.Payload));
        }

        var catalogPayload = catalogResponse.Envelope.Payload;
        var toolId = preferFrameworkTool
            ? GetLiveVisualTreeTool.ResolveToolId(arguments: null, catalogPayload)
            : RemoteAppToolIds.UiGetVisualTree;
        if (toolId is null
            || preferFrameworkTool
               && string.Equals(toolId, RemoteAppToolIds.UiGetVisualTree, StringComparison.Ordinal)
            || !RemoteAppToolCatalog.HasTool(catalogPayload, toolId))
        {
            return LiveUiCaptureResult.Failure(
                preferFrameworkTool
                    ? "The live app does not expose a framework accessibility tree."
                    : "The live app does not expose a native visual tree.");
        }

        return await CaptureWithToolAsync(
            session,
            appToolBridge,
            toolId,
            requestContext,
            cancellationToken);
    }

    private static async Task<LiveUiCaptureResult> CaptureDeviceFirstAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        UiInputRouter uiInputRouter,
        string operationName,
        string? correlationId,
        LiveUiSelector? selector,
        CancellationToken cancellationToken,
        bool allowCached = true,
        string? preferredFallbackToolId = null)
    {
        var deviceResult = await CaptureDeviceAccessibilityAsync(
            session,
            uiInputRouter,
            cancellationToken: cancellationToken,
            allowCached: allowCached).ConfigureAwait(false);
        if (session.CaptureSource == WorkspaceExecutionModes.Device) return deviceResult;
        if (deviceResult.IsSuccess
            && (selector is null || ContainsSelectorEvidence(deviceResult, selector)))
        {
            return deviceResult;
        }
        if (deviceResult.IsSuccess && HasModalDominance(deviceResult))
        {
            return deviceResult;
        }

        var fallbackResult = preferredFallbackToolId is null ? null
            : await CaptureWithToolAsync(session, appToolBridge, preferredFallbackToolId,
                new AppToolBridgeRequestContext("host-ui", operationName, correlationId), cancellationToken).ConfigureAwait(false);
        if (fallbackResult is null || !fallbackResult.IsSuccess
            || selector is not null && !ContainsSelectorEvidence(fallbackResult, selector))
            fallbackResult = await CaptureAsync(
            session,
            appToolBridge,
            operationName,
            correlationId,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (fallbackResult.IsSuccess)
        {
            return AddAttemptedToolIds(
                fallbackResult,
                [DeviceAccessibilityToolId, .. fallbackResult.AttemptedToolIds]);
        }

        return deviceResult.IsSuccess
            ? deviceResult with
            {
                AttemptedToolIds = [DeviceAccessibilityToolId, .. fallbackResult.AttemptedToolIds]
            }
            : AddAttemptedToolIds(
                fallbackResult,
                [DeviceAccessibilityToolId, .. fallbackResult.AttemptedToolIds]);
    }

    internal static Task<LiveUiCaptureResult> CaptureForInteractionAsync(
        AppSessionSnapshot session, IAppToolBridge appToolBridge, UiInputRouter uiInputRouter,
        string actionId, LiveUiSelector? selector, string? preferredFallbackToolId,
        CancellationToken cancellationToken)
        => CaptureDeviceFirstAsync(session, appToolBridge, uiInputRouter, "interact", actionId,
            selector, cancellationToken, allowCached: false, preferredFallbackToolId);

    private static async Task<LiveUiCaptureResult> CaptureAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string operationName,
        string? correlationId,
        LiveUiSelector? selector,
        CancellationToken cancellationToken)
    {
        if (session.CaptureSource == WorkspaceExecutionModes.Device)
            return LiveUiCaptureResult.Failure("App visual trees require the Ansight SDK. Use device accessibility or screenshot text.");
        var requestContext = new AppToolBridgeRequestContext("host-ui", operationName, correlationId);
        var catalogResponse = await appToolBridge.QueryToolsAsync(
            session.SessionId,
            cancellationToken,
            requestContext);
        if (!catalogResponse.Success || catalogResponse.Envelope is null)
        {
            return LiveUiCaptureResult.Failure(catalogResponse.Message);
        }

        if (string.Equals(catalogResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return LiveUiCaptureResult.Failure(ReadError(catalogResponse.Envelope.Payload));
        }

        var catalogPayload = catalogResponse.Envelope.Payload;
        var toolId = GetLiveVisualTreeTool.ResolveToolId(arguments: null, catalogPayload);
        if (toolId is null)
        {
            return LiveUiCaptureResult.Failure(
                "The live app does not expose a supported visual-tree capture tool.");
        }

        var preferredResult = await CaptureWithToolAsync(
            session,
            appToolBridge,
            toolId,
            requestContext,
            cancellationToken);
        if (!ShouldTryNativeFallback(preferredResult, toolId, catalogPayload, selector))
        {
            return preferredResult;
        }

        var nativeResult = await CaptureWithToolAsync(
            session,
            appToolBridge,
            RemoteAppToolIds.UiGetVisualTree,
            requestContext,
            cancellationToken);
        var attemptedToolIds = new[] { toolId, RemoteAppToolIds.UiGetVisualTree };
        if (!nativeResult.IsSuccess)
        {
            return preferredResult with { AttemptedToolIds = attemptedToolIds };
        }

        if (!preferredResult.IsSuccess
            || preferredResult.Capture is { } preferredCapture && ContainsCycle(preferredCapture)
            || selector is not null && ContainsSelectorEvidence(nativeResult, selector))
        {
            return nativeResult with { AttemptedToolIds = attemptedToolIds };
        }

        return preferredResult with { AttemptedToolIds = attemptedToolIds };
    }

    private static async Task<LiveUiCaptureResult> CaptureWithToolAsync(
        AppSessionSnapshot session,
        IAppToolBridge appToolBridge,
        string toolId,
        AppToolBridgeRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var visualTreeArguments = string.Equals(
            toolId,
            RemoteAppToolIds.MauiGetVisualTree,
            StringComparison.Ordinal)
            ? new JsonObject
            {
                ["root"] = MauiVisualTreeRootScope.Window
            }
            : null;
        if (!GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
                visualTreeArguments,
                toolId,
                out var remoteArguments,
                out var argumentError))
        {
            return LiveUiCaptureResult.Failure(
                argumentError ?? "Could not build visual-tree capture arguments.");
        }

        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            session.SessionId,
            toolId,
            remoteArguments,
            after: null,
            cancellationToken,
            requestContext);
        if (!response.Success || response.Envelope is null)
        {
            return LiveUiCaptureResult.Failure(response.Message);
        }

        if (string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return LiveUiCaptureResult.Failure(ReadError(response.Envelope.Payload));
        }

        if (response.Envelope.Payload is not JsonObject envelopePayload
            || envelopePayload["result"] is not JsonObject rawResult)
        {
            return LiveUiCaptureResult.Failure("The visual-tree response did not contain an object result.");
        }

        var payload = rawResult.DeepClone() as JsonObject ?? rawResult;
        return CreateCapture(session, toolId, payload);
    }

    private static LiveUiCaptureResult CreateCapture(
        AppSessionSnapshot session,
        string toolId,
        JsonObject rawPayload,
        string? failureContext = null)
    {
        var payload = VisualTreePresentationNormalizer.Create(rawPayload);
        if (!VisualTreeTypeRegistry.TryCreateCompactV2(payload, out var typeRegistry, out var root)
            || typeRegistry is null
            || root is null)
        {
            return LiveUiCaptureResult.Failure(
                string.IsNullOrWhiteSpace(failureContext)
                    ? "The visual-tree response was not a valid compact v2 payload."
                    : $"{failureContext} The result was not a valid compact v2 payload.");
        }

        var bounds = LiveUiNodeQuery.Enumerate(root, typeRegistry)
            .Select(match => LiveUiNodeQuery.ReadBounds(match.Node))
            .OfType<LiveUiBounds>()
            .Where(candidate => candidate.Width > 0 && candidate.Height > 0)
            .ToArray();
        var viewport = LiveUiNodeQuery.ReadCoordinateSpace(payload)
                       ?? LiveUiNodeQuery.ReadBounds(root)
                       ?? LiveUiBounds.Union(bounds);
        var capturedAtUtc = ReadDateTimeOffset(payload, "capturedAtUtc") ?? DateTimeOffset.UtcNow;
        return LiveUiCaptureResult.Success(new LiveUiTreeCapture(
            session,
            toolId,
            rawPayload,
            payload,
            root,
            typeRegistry,
            viewport,
            capturedAtUtc));
    }

    private static LiveUiCaptureResult AddAttemptedToolIds(
        LiveUiCaptureResult result,
        IReadOnlyList<string> attemptedToolIds)
        => result with
        {
            AttemptedToolIds = attemptedToolIds
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

    private static bool ShouldTryNativeFallback(
        LiveUiCaptureResult preferredResult,
        string preferredToolId,
        JsonNode? catalogPayload,
        LiveUiSelector? selector)
    {
        if (string.Equals(preferredToolId, RemoteAppToolIds.UiGetVisualTree, StringComparison.Ordinal)
            || !RemoteAppToolCatalog.HasTool(catalogPayload, RemoteAppToolIds.UiGetVisualTree))
        {
            return false;
        }

        return !preferredResult.IsSuccess
               || preferredResult.Capture is { } capture && ContainsCycle(capture)
               || selector is not null && !ContainsMatch(preferredResult, selector);
    }

    private static bool ContainsMatch(LiveUiCaptureResult result, LiveUiSelector selector)
        => result.Capture is { } capture
           && selector.IsAvailable(LiveUiNodeQuery.Find(
               capture.Root,
               selector,
               capture.TypeRegistry).Count);

    private static bool ContainsSelectorEvidence(
        LiveUiCaptureResult result,
        LiveUiSelector selector)
        => ContainsMatch(result, selector)
           || selector.Visible == true && ContainsMatch(result, selector.WithoutVisibility());

    private static bool HasModalDominance(LiveUiCaptureResult result)
        => result.Capture is { } capture
           && LiveUiNodeQuery.Enumerate(capture.Root, capture.TypeRegistry)
               .Any(IsModalSurfaceNode);

    private static bool IsModalSurfaceNode(LiveUiNodeMatch match)
    {
        var automationId = LiveUiNodeQuery.ReadAutomationId(match.Node);
        if (string.Equals(automationId, "PopoverDismissRegion", StringComparison.OrdinalIgnoreCase)
            || automationId?.EndsWith("-close-button", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var role = LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry);
        if (role is not null
            && (role.Equals("dialog", StringComparison.OrdinalIgnoreCase)
                || role.Equals("alert", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var type = match.TypeRegistry.Resolve(match.Node);
        if (type is not null
            && (type.Contains("Dialog", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Alert", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Popover", StringComparison.OrdinalIgnoreCase)
                || type.Contains("ActionSheet", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var text = LiveUiNodeQuery.ReadText(match.Node);
        return string.Equals(text, "Sheet Grabber", StringComparison.OrdinalIgnoreCase)
               || text?.Contains("dismiss pop-up window", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsCycle(LiveUiTreeCapture capture)
    {
        if (capture.Payload["flagBits"] is not JsonObject flagBits
            || flagBits["cycle"] is not JsonValue cycleValue
            || !cycleValue.TryGetValue<int>(out var cycleFlag)
            || cycleFlag <= 0)
        {
            return false;
        }

        return LiveUiNodeQuery.Enumerate(capture.Root, capture.TypeRegistry)
            .Any(match => match.Node["flags"] is JsonValue flagsValue
                          && flagsValue.TryGetValue<int>(out var flags)
                          && (flags & cycleFlag) == cycleFlag);
    }

    public string? ResolveNativeDeviceIdentifier()
        => DeviceLifecycleTool.ResolveNativeDeviceIdentifier(Session);

    private static DateTimeOffset? ReadDateTimeOffset(JsonObject payload, string propertyName)
    {
        var text = LiveUiNodeQuery.ReadString(payload, propertyName);
        return DateTimeOffset.TryParse(text, out var value) ? value.ToUniversalTime() : null;
    }

    private static string ReadError(JsonNode? payload)
        => payload is JsonObject error
            ? LiveUiNodeQuery.ReadString(error, "message") ?? "The app rejected visual-tree capture."
            : "The app rejected visual-tree capture.";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}

internal sealed record LiveUiCaptureResult(
    bool IsSuccess,
    LiveUiTreeCapture? Capture,
    string Message)
{
    public IReadOnlyList<string> AttemptedToolIds { get; init; } = [];

    public static LiveUiCaptureResult Success(LiveUiTreeCapture capture)
        => new(true, capture, string.Empty)
        {
            AttemptedToolIds = [capture.ToolId]
        };

    public static LiveUiCaptureResult Failure(string message)
        => new(false, null, message);
}
