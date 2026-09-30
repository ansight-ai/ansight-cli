using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations;

[Export(typeof(IOperationDispatcher))]
internal sealed class OperationDispatcher : IOperationDispatcher
{
    private readonly IRuntimeState runtimeState;
    private readonly IApplicationPaths applicationPaths;
    private readonly ProductAnalytics analytics;
    private readonly IAppToolBridge appToolBridge;
    private readonly ToolCatalog toolCatalog;
    private readonly DotNetToolCatalog dotNetToolCatalog;
    private readonly NativeProfilingToolCatalog nativeProfilingToolCatalog;
    private readonly DotNetProfilingEngine dotNetProfilingService;
    private readonly AudioInjectionRouter audioInjectionRouter = new();
    private readonly UiInputRouter uiInputRouter = new();
    private readonly DeviceLocationRouter deviceLocationRouter = new();
    private readonly DeviceLifecycleRouter deviceLifecycleRouter = new();
    private readonly DevicePermissionsRouter devicePermissionsRouter = new();
    private readonly RepositoryTaskRouter repositoryTaskRouter;
    private readonly IExternalSessionScreenshotCaptureManager? externalSessionScreenshotCaptureManager;

    [ImportingConstructor]
    public OperationDispatcher(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IKnownAppStore knownAppStore,
        IPairingConfigService pairingConfigService,
        IPairingConfigCache pairingConfigCache,
        AppService hostAppService,
        PairingService hostPairingService,
        DotNetProfilingService hostDotNetProfilingService,
        NativeProfilingService hostNativeProfilingService,
        IAppToolBridge appToolBridge,
        [Import(AllowDefault = true)] ICloudSessionSharingService? cloudSessionSharingService = null,
        [Import(AllowDefault = true)] IExternalSessionScreenshotCaptureManager? externalSessionScreenshotCaptureManager = null,
        [Import(AllowDefault = true)] DeviceSessionEvidence? deviceEvidence = null)
    {
        this.runtimeState = runtimeState;
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
        this.applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        analytics = ProductAnalytics.For(applicationPaths);
        this.externalSessionScreenshotCaptureManager = externalSessionScreenshotCaptureManager;
        repositoryTaskRouter = new RepositoryTaskRouter(applicationPaths);

        var sessionResolver = new SessionResolver(runtimeState, appToolBridge);
        toolCatalog = new ToolCatalog(
            runtimeState,
            applicationPaths,
            knownAppStore,
            pairingConfigService,
            pairingConfigCache,
            appToolBridge,
            cloudSessionSharingService,
            sessionResolver,
            uiInputRouter,
            deviceLocationRouter,
            deviceLifecycleRouter,
            repositoryTaskRouter,
            hostAppService,
            hostPairingService,
            audioInjectionRouter,
            deviceEvidence,
            externalSessionScreenshotCaptureManager,
            devicePermissionsRouter);
        repositoryTaskRouter.ConfigureToolExecutor(CallToolAsync);
        repositoryTaskRouter.PublishEvent = (sessionId, label, eventType, group) =>
            HostSessionEvents.Publish(runtimeState, sessionId, label, eventType, group);
        dotNetProfilingService = hostDotNetProfilingService.InternalService;
        dotNetToolCatalog = new DotNetToolCatalog(new DotNetOperationServices(
            dotNetProfilingService));
        nativeProfilingToolCatalog = new NativeProfilingToolCatalog(
            new NativeProfileOperationServices(hostNativeProfilingService));
        repositoryTaskRouter.ConfigureHostToolRegistry(dotNetToolCatalog.HostToolRegistry);
        repositoryTaskRouter.ConfigureHostToolRegistry(nativeProfilingToolCatalog.HostToolRegistry);
    }

    public IReadOnlyList<AppSessionSnapshot> GetSessionSummaries()
        => runtimeState.GetSessionSummaries();

    public void Dispose()
    {
        dotNetProfilingService.Dispose();
    }

    public void ConfigureOptionalExtensions(Ansight.Infrastructure.Extensions.OptionalExtensions extensions) => toolCatalog.ConfigureExtensions(extensions);

    public void ConfigureDevicePermissions(IDeviceService devices, RuntimeOptions options)
        => devicePermissionsRouter.Configure(new NativePermissionService(devices.ListAsync, new DeviceCommandRunner(), options));

    public void ConfigureAudioInjection(AudioInjectionEngine engine) => audioInjectionRouter.Configure(engine);

    public void ConfigureUiInputDriver(IUiInputDriver? driver)
    {
        uiInputRouter.Configure(driver);
    }

    public IAppInteractionContext CreateAppInteractionContext(string sessionId, string? repositoryRootPath = null)
        => new AppInteractionContext(new AppInteractionBackend(
            runtimeState, applicationPaths, appToolBridge, uiInputRouter, sessionId, this, repositoryRootPath, externalSessionScreenshotCaptureManager));

    public void ConfigureUiAccessibilityDriver(IUiAccessibilityDriver? driver)
    {
        uiInputRouter.ConfigureAccessibility(driver);
    }

    public void ConfigureDeviceLocationPlayback(DeviceLocationPlaybackService playback, IDeviceService devices)
        => deviceLocationRouter.ConfigurePlayback(playback, devices);

    public void ConfigureDeviceLocationDriver(IDeviceLocationDriver? driver)
    {
        deviceLocationRouter.Configure(driver);
    }

    public void ConfigureDeviceLifecycleDriver(IDeviceLifecycleDriver? driver)
    {
        deviceLifecycleRouter.Configure(driver);
    }

    public void ConfigureRepositoryTaskRuntime(string executablePath)
    {
        repositoryTaskRouter.ConfigureRuntime(executablePath);
    }

    public RepositoryTaskCatalog InspectRepositoryTasks(string repositoryRootPath, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedRootPath = Path.GetFullPath(repositoryRootPath.Trim());
        var normalizedAppId = appId.Trim();
        var result = repositoryTaskRouter.Load(normalizedRootPath, normalizedAppId);
        return new RepositoryTaskCatalog(
            normalizedRootPath,
            normalizedAppId,
            result.Tasks.Select(static task => task.ToPublicDefinition()).ToArray(),
            result.Warnings);
    }

    public async Task<RepositoryTaskRunResult> RunRepositoryTaskAsync(
        string repositoryRootPath,
        string appId,
        string sessionId,
        string taskId,
        JsonObject? suppliedInput,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? secretValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        var normalizedRootPath = Path.GetFullPath(repositoryRootPath.Trim());
        var normalizedAppId = appId.Trim();
        var normalizedSessionId = sessionId.Trim();
        var normalizedTaskId = taskId.Trim();
        if (!runtimeState.TryGetSessionSnapshot(normalizedSessionId, out var session)
            || session is null)
        {
            return CreateRejectedTaskResult(
                normalizedRootPath,
                normalizedAppId,
                normalizedSessionId,
                normalizedTaskId,
                suppliedInput,
                $"No Ansight session named '{normalizedSessionId}' exists.");
        }

        if (!string.Equals(session.AppId, normalizedAppId, StringComparison.Ordinal))
        {
            return CreateRejectedTaskResult(
                normalizedRootPath,
                normalizedAppId,
                normalizedSessionId,
                normalizedTaskId,
                suppliedInput,
                $"Session '{normalizedSessionId}' belongs to app '{session.AppId}', not '{normalizedAppId}'.");
        }

        if (!appToolBridge.IsSessionConnected(normalizedSessionId) && !runtimeState.IsDeviceSessionActive(normalizedSessionId))
        {
            return CreateRejectedTaskResult(
                normalizedRootPath,
                normalizedAppId,
                normalizedSessionId,
                normalizedTaskId,
                suppliedInput,
                $"Session '{normalizedSessionId}' is not connected. Open the app in a simulator and wait for its Ansight session.");
        }

        var loadResult = repositoryTaskRouter.Load(normalizedRootPath, normalizedAppId);
        var task = loadResult.Tasks.FirstOrDefault(candidate => string.Equals(
            candidate.TaskId,
            normalizedTaskId,
            StringComparison.Ordinal));
        if (task is null)
        {
            var warnings = loadResult.Warnings.Count == 0
                ? string.Empty
                : $" {string.Join(" ", loadResult.Warnings)}";
            return CreateRejectedTaskResult(
                normalizedRootPath,
                normalizedAppId,
                normalizedSessionId,
                normalizedTaskId,
                suppliedInput,
                $"No repository task named '{normalizedTaskId}' exists for app '{normalizedAppId}'.{warnings}");
        }

        if (!RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
                task.InputSchema,
                suppliedInput,
                out var input,
                out var validationError))
        {
            return CreateRejectedTaskResult(
                normalizedRootPath,
                normalizedAppId,
                normalizedSessionId,
                normalizedTaskId,
                suppliedInput,
                validationError);
        }

        return await repositoryTaskRouter.ExecuteAsync(
                task,
                normalizedSessionId,
                input,
                $"ansight-task-test-{Guid.NewGuid():N}",
                cancellationToken,
                secretValues: secretValues)
            .ConfigureAwait(false);
    }

    private static RepositoryTaskRunResult CreateRejectedTaskResult(
        string repositoryRootPath,
        string appId,
        string sessionId,
        string taskId,
        JsonObject? suppliedInput,
        string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new RepositoryTaskRunResult(
            Guid.NewGuid().ToString("N"),
            repositoryRootPath,
            appId,
            sessionId,
            taskId,
            RepositoryTaskRunStatus.Rejected,
            now,
            now,
            0,
            message,
            suppliedInput?.DeepClone().AsObject() ?? new JsonObject(),
            null,
            [],
            [],
            string.Empty);
    }

    public JsonObject BuildToolsListResult()
    {
        var tools = new JsonArray();
        AddToolDefinitions(tools, toolCatalog.BuildToolsListResult());
        AddToolDefinitions(tools, dotNetToolCatalog.BuildToolsListResult());
        AddToolDefinitions(tools, nativeProfilingToolCatalog.BuildToolsListResult());
        return new JsonObject { ["tools"] = tools };
    }

    public bool TryGetSessionSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
    {
        return runtimeState.TryGetSessionSnapshot(sessionId, out snapshot);
    }

    public bool IsSessionConnected(string sessionId)
    {
        return runtimeState.IsDeviceSessionActive(sessionId) || appToolBridge.IsSessionConnected(sessionId);
    }

    public async Task<JsonObject?> GetSessionAppToolCatalogAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (runtimeState.IsDeviceSessionActive(sessionId)) return null;
        var response = await appToolBridge.QueryToolsAsync(
                sessionId.Trim(),
                cancellationToken,
                new AppToolBridgeRequestContext(
                    "simulator-agent",
                    "context-capabilities",
                    null))
            .ConfigureAwait(false);
        if (!response.Success
            || response.Envelope is null
            || string.Equals(response.Envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)
            || response.Envelope.Payload is not JsonObject catalog)
        {
            return null;
        }

        return catalog.DeepClone().AsObject();
    }

    public Task<RequestResult> CallToolAsync(
        string toolName,
        JsonObject? arguments,
        string? correlationId = null,
        OperationExecutionContext? context = null)
    {
        var feature = UsageFeature(toolName);
        return feature is null ? CallToolMeasuredCoreAsync(toolName, arguments, correlationId, context)
            : analytics.ObserveUsageAsync(feature,
                () => CallToolMeasuredCoreAsync(toolName, arguments, correlationId, context),
                result => result.IsError || result.Payload?["isError"]?.GetValue<bool>() == true ? "failed" : "succeeded");
    }

    private static string? UsageFeature(string? name) => name switch
    {
        "ansight_get_logs" or "ansight_search_logs" => "logs",
        "ansight_get_network_requests" or "ansight_get_network_request" or "ansight_read_network_body" => "network",
        "ansight_get_visual_tree" => "visual_tree",
        "ansight_get_annotations" or "ansight_inject_annotation" or "ansight_update_annotation" or "ansight_delete_annotation" => "annotations",
        "ansight_export_session" => "export",
        "ansight_compare_sessions" => "session",
        // Only category constants leave this boundary, never app-defined tool names or arguments.
        _ when name?.StartsWith("ansight_ui_", StringComparison.Ordinal) == true => "ui",
        _ when name?.StartsWith("ansight_keyboard_", StringComparison.Ordinal) == true => "keyboard",
        _ when name?.StartsWith("ansight_audio_", StringComparison.Ordinal) == true => "audio",
        _ when name?.StartsWith("ansight_dotnet_", StringComparison.Ordinal) == true => "profile",
        _ when name?.StartsWith("ansight_native_", StringComparison.Ordinal) == true => "profile",
        _ => null
    };

    private Task<RequestResult> CallToolMeasuredCoreAsync(
        string toolName, JsonObject? arguments, string? correlationId, OperationExecutionContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var normalizedToolName = toolName.Trim();
        var parameters = new JsonObject
        {
            ["name"] = normalizedToolName,
            ["arguments"] = arguments?.DeepClone()
        };
        return nativeProfilingToolCatalog.Contains(normalizedToolName)
            ? nativeProfilingToolCatalog.HandleToolsCallAsync(parameters, correlationId)
            : dotNetToolCatalog.Contains(normalizedToolName)
            ? dotNetToolCatalog.HandleToolsCallAsync(parameters, correlationId)
            : toolCatalog.HandleToolsCallAsync(parameters, correlationId, context);
    }

    private static void AddToolDefinitions(JsonArray destination, JsonObject source)
    {
        if (source["tools"] is not JsonArray definitions)
        {
            return;
        }

        foreach (var definition in definitions)
        {
            destination.Add(definition?.DeepClone());
        }
    }

    public IDisposable BeginSimulatorAgentRun(string sessionId, string? targetDeviceIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var normalizedSessionId = sessionId.Trim();
        var targetScope = uiInputRouter.BeginTargetScope(normalizedSessionId, targetDeviceIdentifier);
        try
        {
            var captureScope = externalSessionScreenshotCaptureManager?.BeginTestRun(normalizedSessionId)
                               ?? throw new InvalidOperationException(
                                   "Tests require screenshot evidence capture, but capture is unavailable.");
            return new SimulatorAgentRunScope(targetScope, captureScope);
        }
        catch
        {
            targetScope.Dispose();
            throw;
        }
    }

    private sealed class SimulatorAgentRunScope(
        IDisposable targetScope,
        IDisposable captureScope) : IDisposable
    {
        private IDisposable? targetScope = targetScope;
        private IDisposable? captureScope = captureScope;

        public void Dispose()
        {
            Interlocked.Exchange(ref captureScope, null)?.Dispose();
            Interlocked.Exchange(ref targetScope, null)?.Dispose();
        }
    }

}
