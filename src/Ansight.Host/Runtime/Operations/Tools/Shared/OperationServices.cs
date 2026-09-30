using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure;

namespace Ansight.Host.Runtime.Operations.Tools.Shared;

internal sealed class OperationServices
{

    public OperationServices(
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
        DevicePermissionsRouter? devicePermissionsRouter = null)
    {
        DevicePermissionsRouter = devicePermissionsRouter ?? new DevicePermissionsRouter();
        DeviceEvidence = deviceEvidence;
        ExternalScreenshots = externalScreenshots;
        AudioInjectionRouter = audioInjectionRouter ?? new AudioInjectionRouter();
        RuntimeState = runtimeState;
        ApplicationPaths = applicationPaths;
        KnownAppStore = knownAppStore;
        PairingConfigService = pairingConfigService;
        PairingConfigCache = pairingConfigCache;
        AppService = hostAppService
                         ?? new AppService(knownAppStore, runtimeState, appToolBridge, pairingConfigCache);
        PairingService = hostPairingService
                             ?? new PairingService(pairingConfigService, pairingConfigCache, knownAppStore);
        AppToolBridge = appToolBridge;
        CloudSessionSharingService = cloudSessionSharingService;
        SessionResolver = sessionResolver;
        UiInputRouter = uiInputRouter ?? new UiInputRouter();
        DeviceLocationRouter = deviceLocationRouter ?? new DeviceLocationRouter();
        DeviceLifecycleRouter = deviceLifecycleRouter ?? new DeviceLifecycleRouter();
        RepositoryTaskRouter = repositoryTaskRouter
                               ?? new RepositoryTaskRouter(applicationPaths);
    }

    public IExternalSessionScreenshotCaptureManager? ExternalScreenshots { get; }

    public DeviceSessionEvidence? DeviceEvidence { get; }

    public AudioInjectionRouter AudioInjectionRouter { get; }

    public IRuntimeState RuntimeState { get; }

    public IApplicationPaths ApplicationPaths { get; }

    public IKnownAppStore KnownAppStore { get; }

    public IPairingConfigService PairingConfigService { get; }

    public IPairingConfigCache PairingConfigCache { get; }

    public AppService AppService { get; }

    public PairingService PairingService { get; }

    public IAppToolBridge AppToolBridge { get; }

    public ICloudSessionSharingService? CloudSessionSharingService { get; }

    public SessionResolver SessionResolver { get; }

    public UiInputRouter UiInputRouter { get; }

    public DeviceLocationRouter DeviceLocationRouter { get; }

    public DevicePermissionsRouter DevicePermissionsRouter { get; }

    public DeviceLifecycleRouter DeviceLifecycleRouter { get; }

    public RepositoryTaskRouter RepositoryTaskRouter { get; }

    public void AttachHostToolRegistry(OperationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        RepositoryTaskRouter.ConfigureHostToolRegistry(registry);
    }
}
