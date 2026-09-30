using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Tools;

namespace Ansight.Host.UiAutomation;

public sealed class LiveVisualTreeCaptureService
{
    private readonly ISessionReader sessionReader;
    private readonly ISessionIngestion sessionIngestion;
    private readonly IAppToolBridge appToolBridge;
    private readonly UiInputRouter accessibilityRouter = new();

    internal LiveVisualTreeCaptureService(
        ISessionReader sessionReader,
        ISessionIngestion sessionIngestion,
        IAppToolBridge appToolBridge,
        IUiAccessibilityDriver? accessibilityDriver = null)
    {
        this.sessionReader = sessionReader ?? throw new ArgumentNullException(nameof(sessionReader));
        this.sessionIngestion = sessionIngestion ?? throw new ArgumentNullException(nameof(sessionIngestion));
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
        ConfigureAccessibilityDriver(accessibilityDriver);
    }

    internal void ConfigureAccessibilityDriver(IUiAccessibilityDriver? driver)
        => accessibilityRouter.ConfigureAccessibility(driver);

    public async Task<LiveVisualTreeSourcesResult> GetSourcesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new(false, "Session ID is required.", []);
        }

        var normalizedSessionId = sessionId.Trim();
        if (!IsLive(normalizedSessionId)
            || !sessionReader.TryGetSessionSnapshot(normalizedSessionId, out var snapshot)
            || snapshot is null)
        {
            return new(false, "The session is no longer connected.", []);
        }

        var catalog = snapshot.CaptureSource == WorkspaceExecutionModes.Device
            ? null
            : await appToolBridge.QueryToolsAsync(
            normalizedSessionId, cancellationToken,
            new AppToolBridgeRequestContext("local-explorer", "list_visual_tree_sources"))
            .ConfigureAwait(false);
        var sources = new List<LiveVisualTreeSource>();
        if (catalog is { Success: true } && catalog.Envelope?.Type != ToolProtocolMessageTypes.ErrorType)
        {
            sources.AddRange(ResolveAppSources(catalog.Envelope?.Payload, SessionPlatformFilters.ResolveSessionPlatformKey(snapshot)));
        }

        var accessibility = await LiveUiTreeCapture.CaptureDeviceAccessibilityAsync(
            snapshot, accessibilityRouter, maxNodes: 1, maxDepth: 1,
            cancellationToken: cancellationToken, allowCached: false).ConfigureAwait(false);
        if (accessibility.Capture is not null)
        {
            sources.Add(new(LiveUiTreeCapture.DeviceAccessibilityToolId, "Accessibility"));
        }

        if (sources.Count == 0 && catalog is not null && (!catalog.Success || catalog.Envelope?.Type == ToolProtocolMessageTypes.ErrorType))
        {
            return new(false, ReadResponseMessage(catalog.Envelope?.Payload, catalog.Message), []);
        }

        return new(true, sources.Count > 0 ? string.Empty : "No visual trees are available for this app.", sources);
    }

    private bool IsLive(string sessionId)
        => appToolBridge.IsSessionConnected(sessionId)
           || sessionReader is ISessionLifecycle lifecycle && lifecycle.IsDeviceSessionActive(sessionId);

    internal static IReadOnlyList<LiveVisualTreeSource> ResolveAppSources(JsonNode? catalog, string? platform)
    {
        var nativeLabel = VisualTreeContract.NormalizeRuntimePlatform(platform) switch
        {
            VisualTreeContract.IosRuntimePlatform => "iOS tree",
            VisualTreeContract.AndroidRuntimePlatform => "Android tree",
            _ => "Native tree"
        };
        LiveVisualTreeSource[] supported =
        [
            new(VisualTreeContract.MauiToolId, "MAUI"),
            new(VisualTreeContract.ReactShadowToolId, "React shadow tree"),
            new(VisualTreeContract.ReactComponentToolId, "React component tree"),
            new(VisualTreeContract.FlutterToolId, "Flutter widget tree"),
            new(VisualTreeContract.DomToolId, "DOM"),
            new(VisualTreeContract.NativeToolId, nativeLabel)
        ];
        return supported.Where(source => RemoteAppToolCatalog.HasTool(catalog, source.ToolId)).ToArray();
    }

    public Task<LiveVisualTreeCaptureResult> CaptureLiveVisualTreeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => CaptureLiveVisualTreeAsync(sessionId, null, cancellationToken);

    public async Task<LiveVisualTreeCaptureResult> CaptureLiveVisualTreeAsync(
        string sessionId,
        string? requestedToolId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Failure(sessionId, "Session ID is required.");
        }

        var normalizedSessionId = sessionId.Trim();
        if (!IsLive(normalizedSessionId))
        {
            return Failure(normalizedSessionId, $"Session '{normalizedSessionId}' is not connected.");
        }

        if (!sessionReader.TryGetSessionSnapshot(normalizedSessionId, out var snapshot) || snapshot is null)
        {
            return Failure(normalizedSessionId, $"Session '{normalizedSessionId}' was not found.");
        }

        if (requestedToolId == LiveUiTreeCapture.DeviceAccessibilityToolId
            || snapshot.CaptureSource == WorkspaceExecutionModes.Device && string.IsNullOrWhiteSpace(requestedToolId))
        {
            var accessibility = await LiveUiTreeCapture.CaptureDeviceAccessibilityAsync(
                snapshot, accessibilityRouter, maxNodes: 2000, maxDepth: 64,
                cancellationToken: cancellationToken, allowCached: false).ConfigureAwait(false);
            return accessibility.Capture is { } capture
                ? Persist(snapshot, capture.ToolId, capture.Payload)
                : Failure(normalizedSessionId, accessibility.Message);
        }

        if (snapshot.CaptureSource == WorkspaceExecutionModes.Device)
            return Failure(normalizedSessionId, "App visual trees require an embedded SDK. Use Accessibility for external captures. " + ExecutionCapabilities.SdkSetupMessage);

        var catalogResponse = await appToolBridge.QueryToolsAsync(
                normalizedSessionId,
                cancellationToken,
                new AppToolBridgeRequestContext("local-explorer", "capture_live_visual_tree"))
            .ConfigureAwait(false);
        if (!catalogResponse.Success || catalogResponse.Envelope is null)
        {
            return Failure(normalizedSessionId, catalogResponse.Message);
        }

        if (string.Equals(catalogResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return Failure(
                normalizedSessionId,
                ReadResponseMessage(catalogResponse.Envelope.Payload, catalogResponse.Message));
        }

        var toolId = GetLiveVisualTreeTool.ResolveToolId(
            new JsonObject { ["toolId"] = requestedToolId }, catalogResponse.Envelope.Payload);
        if (toolId is null)
        {
            return Failure(
                normalizedSessionId,
                "The requested visual tree is not available for this app.");
        }

        if (!GetLiveVisualTreeTool.TryBuildVisualTreeArguments(
                null,
                toolId,
                out var visualTreeArguments,
                out var argumentError))
        {
            return Failure(
                normalizedSessionId,
                argumentError ?? "Unable to build visual-tree capture arguments.");
        }

        var forwardedArguments = AppToolPayloadNormalizer.ApplyHostToolDefaults(
            toolId,
            visualTreeArguments,
            OperationDefaults.DefaultVisualTreeMaxDepth,
            OperationDefaults.DefaultScreenshotQuality,
            OperationDefaults.DefaultScreenshotMaxWidth,
            out _);
        var toolResponse = await appToolBridge.CallToolWithCatalogRecoveryAsync(
                normalizedSessionId,
                toolId,
                forwardedArguments,
                after: null,
                cancellationToken,
                new AppToolBridgeRequestContext("local-explorer", "capture_live_visual_tree"))
            .ConfigureAwait(false);
        if (!toolResponse.Success || toolResponse.Envelope is null)
        {
            return Failure(normalizedSessionId, toolResponse.Message);
        }

        if (string.Equals(toolResponse.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal))
        {
            return Failure(
                normalizedSessionId,
                ReadResponseMessage(toolResponse.Envelope.Payload, toolResponse.Message));
        }

        if (toolResponse.Envelope.Payload is not JsonObject responsePayload
            || responsePayload["result"] is not JsonObject visualTreePayload)
        {
            return Failure(normalizedSessionId, "The app returned an invalid visual-tree payload.");
        }

        return Persist(snapshot, toolId, visualTreePayload);
    }

    private LiveVisualTreeCaptureResult Persist(AppSessionSnapshot snapshot, string toolId, JsonObject visualTreePayload)
    {
        var persisted = SessionVisualTreePersistence.Persist(
            sessionIngestion,
            snapshot,
            toolId,
            visualTreePayload);
        return persisted.IsSuccess && persisted.Snapshot is not null
            ? new LiveVisualTreeCaptureResult(
                true,
                "Captured a live visual-tree snapshot.",
                snapshot.SessionId,
                persisted.Snapshot.SnapshotId,
                persisted.Snapshot.NodeCount)
            : Failure(snapshot.SessionId, persisted.Message);
    }

    private static LiveVisualTreeCaptureResult Failure(string? sessionId, string message)
        => new(false, message, sessionId?.Trim() ?? string.Empty, null, 0);

    private static string ReadResponseMessage(JsonNode? payload, string fallback)
        => payload is JsonObject responsePayload
           && responsePayload["message"] is JsonValue messageValue
           && messageValue.TryGetValue<string>(out var message)
           && !string.IsNullOrWhiteSpace(message)
            ? message.Trim()
            : fallback;
}
