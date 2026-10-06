using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure;
using static Ansight.Host.Runtime.Operations.Tools.Shared.OperationRegistration;

namespace Ansight.Host.Runtime.Operations;

internal sealed class ToolCatalog
{
    private readonly OperationRegistry tools;
    private readonly OperationServices services;
    private Ansight.Infrastructure.Extensions.OptionalExtensions? extensions;
    internal void ConfigureExtensions(Ansight.Infrastructure.Extensions.OptionalExtensions extensions) => this.extensions = extensions;

    public ToolCatalog(
        IRuntimeState runtimeState,
        IApplicationPaths applicationPaths,
        IKnownAppStore knownAppStore,
        IPairingConfigService pairingConfigService,
        IPairingConfigCache pairingConfigCache,
        IAppToolBridge appToolBridge,
        ICloudSessionSharingService? cloudSessionSharingService,
        SessionResolver sessionResolver,
        UiInputRouter? uiInputRouter = null,
        DeviceLocationRouter? deviceLocationRouter = null,
        DeviceLifecycleRouter? deviceLifecycleRouter = null,
        RepositoryTaskRouter? repositoryTaskRouter = null,
        AppService? hostAppService = null,
        PairingService? hostPairingService = null,
        AudioInjectionRouter? audioInjectionRouter = null,
        DeviceSessionEvidence? deviceEvidence = null,
        IExternalSessionScreenshotCaptureManager? externalScreenshots = null,
        DevicePermissionsRouter? devicePermissionsRouter = null,
        DeviceMotionRouter? deviceMotionRouter = null)
    {
        services = new OperationServices(
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
            externalScreenshots,
            devicePermissionsRouter,
            deviceMotionRouter);
        tools = CreateToolRegistry(services);
        services.AttachHostToolRegistry(tools);
    }

    public JsonObject BuildToolsListResult()
    {
        return new JsonObject
        {
            ["tools"] = BuildDefinitions()
        };
    }

    public Task<RequestResult> HandleToolsCallAsync(
        JsonObject? parameters,
        string? correlationId = null,
        OperationExecutionContext? context = null)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Task.FromResult(RequestResult.Error(-32602, "tools/call requires a non-empty tool name."));
        }

        var arguments = parameters?["arguments"] as JsonObject;
        if (services.SessionResolver.TryResolveLiveSession(arguments, out var session, out _)
            && ExecutionCapabilities.RejectTool(services.RuntimeState, services.AppToolBridge, session!, toolName) is { } rejection)
            return Task.FromResult(rejection);
        if (extensions?.HasOperation(toolName) == true)
            return extensions.GetService<IOperationExtension>().ExecuteAsync(services, toolName, arguments, correlationId, context);
        return tools.TryGet(toolName, out var tool)
            ? tool!.ExecuteAsync(arguments, correlationId, context)
            : Task.FromResult(RequestResult.Error(-32602, $"Unknown tool '{toolName}'."));
    }

    public bool Contains(string toolName)
        => tools.Contains(toolName) || extensions?.HasOperation(toolName) == true;

    private JsonArray BuildDefinitions()
    {
        var definitions = tools.BuildDefinitions();
        if (extensions is not null) foreach (var definition in extensions.ReadOperationDefinitions()) definitions.Add(definition?.DeepClone());
        return definitions;
    }

    private static OperationRegistry CreateToolRegistry(OperationServices services)
    {
        return new OperationRegistry(new OperationRegistration[]
        {
            TaskSessionBound(new HostSpanTool(services)),
            TaskSessionBound(new ExecutionCapabilitiesTool(services, require: false)),
            TaskSessionBound(new ExecutionCapabilitiesTool(services, require: true)),
            TaskSessionBound(new ExternalSandboxFileTool(services, "list")),
            TaskSessionBound(new ExternalSandboxFileTool(services, "read")),
            TaskSessionBound(new ExternalSandboxFileTool(services, "capture")),
            HostOnly(new ListAppsTool(services)),
            HostOnly(new GetAppTool(services)),
            HostOnly(new RegisterAppTool(services)),
            HostOnly(new IssuePairingConfigTool(services)),
            HostOnly(new ListPairingConfigsTool(services)),
            HostOnly(new GetPairingConfigTool(services)),
            HostOnly(new DeletePairingConfigTool(services)),
            HostOnly(new ListDevicesTool(services)),
            HostOnly(new ListHostDevicesTool(services)),
            HostOnly(new DeviceLifecycleTool(services, DeviceLifecycleAction.StartDevice)),
            TaskSessionBound(new DeviceLifecycleTool(services, DeviceLifecycleAction.LaunchApplication)),
            TaskSessionBound(new DeviceLifecycleTool(services, DeviceLifecycleAction.ForegroundApplication)),
            TaskSessionBound(new DeviceLifecycleTool(services, DeviceLifecycleAction.BackgroundApplication)),
            TaskSessionBound(new DeviceLifecycleTool(services, DeviceLifecycleAction.TerminateApplication)),
            HostOnly(new ListRepositoryTasksTool(services)),
            HostOnly(new DescribeRepositoryModuleTool(services)),
            HostOnly(new RunRepositoryTaskTool(services)),
            HostOnly(new ListSessionsTool(services)),
            TaskSessionBound(new GetAppStateTool(services)),
            TaskSessionBound(new GetSessionPropertiesTool(services)),
            TaskSessionBound(new GetLogsTool(services)),
            TaskSessionBound(new SearchLogsTool(services)),
            TaskSessionBound(new GetLogContextTool(services)),
            TaskSessionBound(new SummarizeLogWindowTool(services)),
            TaskSessionBound(new GetLogFacetsTool(services)),
            TaskSessionBound(new GetLogTimelineTool(services)),
            HostOnly(new ExportLogSliceTool(services)),
            TaskSessionBound(new GetNearestArtifactsTool(services)),
            TaskSessionBound(new ExtractExceptionsTool(services)),
            TaskSessionBound(new GetSessionArtifactsTool(services)),
            HostOnly(new GetSessionStorageTool(services)),
            TaskSessionBound(new GetScreenshotFrameTool(services)),
            TaskSessionBound(new GetVisualTreeSnapshotTool(services)),
            TaskSessionBound(new SearchVisualTreeTool(services)),
            TaskSessionBound(new ListArtifactFilesTool(services)),
            TaskSessionBound(new ReadArtifactFileTool(services)),
            TaskSessionBound(new GetSessionTimelineTool(services)),
            TaskSessionBound(new SummarizeTelemetryWindowTool(services)),
            TaskSessionBound(new GetTelemetryTimelineTool(services)),
            HostOnly(new CompareSessionsTool(services)),
            HostOnly(new ExportSessionBundleTool(services)),
            HostOnly(new ExportSessionArchiveTool(services)),
            HostOnly(new ImportSessionArchiveTool(services)),
            HostOnly(new ExtractSessionTool(services)),
            HostOnly(new TrimSessionRemoveRangeTool(services)),
            HostOnly(new TrimSessionKeepRangeTool(services)),
            TaskSessionBound(new GetTouchTimelineTool(services)),
            TaskSessionBound(new GetTouchContextTool(services)),
            TaskSessionBound(new SummarizeTouchFlowTool(services)),
            TaskSessionBound(new GetGestureSegmentsTool(services)),
            TaskSessionBound(new FindTapTargetsTool(services)),
            TaskSessionBound(new GetTouchHeatmapTool(services)),
            TaskSessionBound(new FindDeadTouchesTool(services)),
            HostOnly(new ExportTouchSliceTool(services)),
            TaskSessionBound(new GetTouchArtifactsTool(services)),
            TaskSessionBound(new GetAnnotationsTool(services)),
            TaskSessionBound(new CreateAnnotationTool(services)),
            TaskSessionBound(new PatchAnnotationTool(services)),
            TaskSessionBound(new RemoveAnnotationTool(services)),
            TaskSessionBound(new GetNetworkRequestsTool(services)),
            TaskSessionBound(new GetNetworkRequestTool(services)),
            TaskSessionBound(new ReadNetworkBodyTool(services)),
            HostOnly(new InjectAnnotationTool(services)),
            HostOnly(new UpdateAnnotationTool(services)),
            HostOnly(new DeleteAnnotationTool(services)),
            HostOnly(new DeleteSessionAnalysisTool(services)),
            HostOnly(new DeleteSessionTool(services)),
            HostOnly(new TagSessionTool(services)),
            TaskSessionBound(new GetTelemetryTool(services)),
            TaskSessionBound(new ListAppToolsTool(services)),
            HostOnly(new CallAppTool(services)),
            HostOnly(new CallAppToolsTool(services)),
            HostOnly(new ForceDisconnectSessionTool(services)),
            TaskSessionBound(new TakeScreenshotTool(services)),
            TaskSessionBound(new GetLiveVisualTreeTool(services)),
            TaskSessionBound(new GetLiveNavigationStructureTool(services)),
            TaskSessionBound(new FindLiveUiTool(services)),
            TaskSessionBound(new ScanLiveScreenTool(services)),
            TaskSessionBound(new WaitForLiveUiTool(services)),
            TaskSessionBound(new AssertLiveUiTool(services)),
            TaskSessionBound(new AssertLiveDatabaseTool(services)),
            TaskSessionBound(new AssertLiveScreenshotTool(services)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.Tap)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.TypeText)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.Swipe)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.Scroll)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.Pinch)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.Back)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.KeyboardOpen)),
            TaskSessionBound(new LiveKeyboardStateTool(services)),
            TaskSessionBound(new LiveUiActionTool(services, LiveUiActionKind.KeyboardDismiss)),
            TaskSessionBound(new LiveUiSequenceTool(services)),
            HostOnly(new PushFileToAppTool(services)),
            HostOnly(new ListFocusedControlsTool(services)),
            HostOnly(new GetFocusedControlTool(services)),
            TaskSessionBound(new AudioInjectionTool(services, inject: false)),
            TaskSessionBound(new AudioInjectionTool(services, inject: true)),
            TaskSessionBound(new PlayDeviceLocationTool(services)),
            TaskSessionBound(new SetDeviceLocationTool(services)),
            TaskSessionBound(new ClearDeviceLocationTool(services)),
            TaskSessionBound(new DeviceMotionTool(services, shake: true)),
            TaskSessionBound(new DeviceMotionTool(services, shake: false)),
            TaskSessionBound(new PermissionTool(services, "grant", null)),
            TaskSessionBound(new PermissionTool(services, "revoke", null)),
            TaskSessionBound(new PermissionTool(services, "query", null)),
            TaskSessionBound(new PermissionTool(services, "grant", "ios")),
            TaskSessionBound(new PermissionTool(services, "revoke", "ios")),
            TaskSessionBound(new PermissionTool(services, "query", "ios")),
            TaskSessionBound(new PermissionTool(services, "reset", "ios")),
            TaskSessionBound(new PermissionTool(services, "grant", "android")),
            TaskSessionBound(new PermissionTool(services, "revoke", "android")),
            TaskSessionBound(new PermissionTool(services, "query", "android"))
        });
    }
}
