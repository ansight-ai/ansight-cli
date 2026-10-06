using System.ComponentModel.Composition;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Sessions;
using Ansight.Host.Telemetry;
using Ansight.Host.UiAutomation;
using Ansight.Host.Workspaces;
using Ansight.Infrastructure.Logging;
using Ansight.Infrastructure.Preferences;
using Ansight.Infrastructure.Security;
using Ansight.Tools;

namespace Ansight.Host.Runtime;

[Export(typeof(RuntimeCoordinator))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed partial class RuntimeCoordinator : IAsyncDisposable, IDisposable
{
    internal CancellationToken FeatureLifetime { get; }
    internal IEncryptedStorage ExtensionStorage { get; }
    internal RuntimeIdentity ExtensionIdentity { get; }
    public Ansight.Infrastructure.Extensions.OptionalExtensions Extensions { get; }
    private static readonly ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private readonly Lock gate = new();
    private readonly MefHostComposition? composition;
    private readonly IDisposable? protocolDefaultsOverride;
    private readonly AudioInjectionEngine audioInjectionEngine;
    private readonly IRuntimeState runtimeState;
    private readonly IUdpPairingServer pairingServer;
    private readonly IAppToolBridge appToolBridge;
    private readonly IOperationDispatcher operationDispatcher;
    private readonly IExternalSessionScreenshotCaptureManager? externalScreenshotCaptures;
    private readonly DeviceSessionEvidence? deviceEvidence;
    private readonly HeadlessHostDeviceDriver? headlessDeviceDriver;
    private readonly string javaScriptExecutablePath;
    private CancellationTokenSource? shutdownTokenSource;
    private Task? pairingTask;
    private Task? analyticsTask;
    private bool disposed;
    private bool started;
    private string[] startupWarnings = [];

    public RuntimeCoordinator()
        : this(options: null)
    {
    }

    [ImportingConstructor]
    internal RuntimeCoordinator(
        IApplicationPaths applicationPaths,
        IRuntimeState runtimeState,
        IUdpPairingServer pairingServer,
        IAppToolBridge appToolBridge,
        IOperationDispatcher operationDispatcher,
        IEncryptedStorage encryptedStorage,
        IIdentityStore hostIdentityStore,
        AppService appService,
        PairingService pairingService,
        DotNetProfilingService dotNetProfilingService,
        NativeProfilingService nativeProfilingService,
        RuntimeOptions hostRuntimeOptions,
        IUserPreferences userPreferences,
        [Import(AllowDefault = true)] IExternalSessionScreenshotCaptureManager? externalScreenshotCaptures = null,
        [Import(AllowDefault = true)] DeviceSessionEvidence? deviceEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(hostRuntimeOptions);
        FeatureLifetime = hostRuntimeOptions.FeatureLifetime;
        SessionVideoEncoder = hostRuntimeOptions.SessionVideoEncoder;
        protocolDefaultsOverride = CreateProtocolDefaultsOverride(hostRuntimeOptions);

        ApplicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        BaseFolderPath = ApplicationPathsFactory.ResolveBaseFolderPath(ApplicationPaths);
        Analytics = new ProductAnalytics(BaseFolderPath);
        ExtensionStorage = encryptedStorage;
        ExtensionIdentity = hostIdentityStore.Current;
        Extensions = new Ansight.Infrastructure.Extensions.OptionalExtensions(this, hostRuntimeOptions.ExtensionsDirectory);
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.pairingServer = pairingServer ?? throw new ArgumentNullException(nameof(pairingServer));
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
        this.operationDispatcher = operationDispatcher ?? throw new ArgumentNullException(nameof(operationDispatcher));
        this.externalScreenshotCaptures = externalScreenshotCaptures;
        this.deviceEvidence = deviceEvidence;
        UserPreferences = userPreferences ?? throw new ArgumentNullException(nameof(userPreferences));
        javaScriptExecutablePath = hostRuntimeOptions.JavaScriptExecutablePath;
        SimulatorAgent = new SimulatorAgentService(
            encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage)),
            this.operationDispatcher,
            ApplicationPaths);
        Ui = new UiAutomationService((name, arguments, correlationId) => this.operationDispatcher.CallToolAsync(name, arguments, correlationId));
        Devices = new DeviceService(hostRuntimeOptions, new IosAudioRouteRecovery(ApplicationPaths.ApplicationDataPath));
        operationDispatcher.ConfigureDevicePermissions(Devices, hostRuntimeOptions);
        audioInjectionEngine = new AudioInjectionEngine(runtimeState, appToolBridge, Devices, hostRuntimeOptions, ApplicationPaths);
        operationDispatcher.ConfigureAudioInjection(audioInjectionEngine);
        Audio = new AudioService((name, arguments, correlationId) => operationDispatcher.CallToolAsync(name, arguments, correlationId));
        headlessDeviceDriver = new HeadlessHostDeviceDriver(Devices, hostRuntimeOptions);
        if (externalScreenshotCaptures is ExternalSessionScreenshotCaptureManager physicalScreenshots)
            physicalScreenshots.ConfigurePhysicalIosCapture(headlessDeviceDriver.CapturePhysicalIosScreenshotAsync);
        this.operationDispatcher.ConfigureUiAccessibilityDriver(headlessDeviceDriver);
        DeviceLocationPlayback = new DeviceLocationPlaybackService(Devices);
        operationDispatcher.ConfigureDeviceLocationPlayback(DeviceLocationPlayback, Devices);
        operationDispatcher.ConfigureDeviceMotionDriver(Devices);
        Identity = CreateIdentityInfo(hostIdentityStore.Current);
        Apps = appService ?? throw new ArgumentNullException(nameof(appService));
        AppTools = new AppToolService(this.appToolBridge,
            id => runtimeState.TryGetSessionContext(id, out var capture) && capture?.CaptureSource == WorkspaceExecutionModes.Device);
        Pairing = pairingService ?? throw new ArgumentNullException(nameof(pairingService));
        Trends = new WorkspaceTrendsService(this, ApplicationPaths);
        Sessions = new SessionQueryService(this.runtimeState);
        SessionEditing = new SessionEditingService(this.runtimeState, this.runtimeState, Trends);
        WorkspaceTests = new WorkspaceTestService(
            this,
            SimulatorAgent,
            new WorkspaceTestTargetLauncher(Devices, Pairing, FindMonitoredSession),
            new CloudWorkspaceTestRunGateway(() => Extensions.GetService<IWorkspaceTestRunGateway>()));
        Profiling = dotNetProfilingService
                    ?? throw new ArgumentNullException(nameof(dotNetProfilingService));
        Profiling.ConfigureDeviceService(Devices);
        NativeProfiling = nativeProfilingService
                          ?? throw new ArgumentNullException(nameof(nativeProfilingService));
        NativeProfiling.ConfigureDeviceService(Devices);
        LocalAppGraphs = new LocalAppGraphService(ApplicationPaths);
        LiveFiles = new LiveSessionFileService(runtimeState, AppTools, id => this.deviceEvidence?.RequireFiles(id),
            async (tool, arguments, token) =>
            {
                using var scope = ToolExecutionCancellation.Push(token);
                return await this.operationDispatcher.CallToolAsync(tool, arguments).ConfigureAwait(false);
            });
        FileVisualizations = new FileVisualizationService(this);
        ArtifactComparisons = new ArtifactComparisonService(this);
        SessionReplays = new SessionReplayService(this);
        SessionArchives = new SessionArchiveService(
            this.runtimeState,
            this.runtimeState,
            ApplicationPaths,
            javaScriptExecutablePath);
        SessionEvidence = new SessionEvidenceService(this.runtimeState, ApplicationPaths);
        SessionCache = new SessionCacheService(this.runtimeState, this.appToolBridge);
        LiveVisualTrees = new LiveVisualTreeCaptureService(
            this.runtimeState,
            this.runtimeState,
            this.appToolBridge,
            headlessDeviceDriver);
        RepositoryAutomations = new RepositoryAutomationService(
            hostRuntimeOptions,
            this.appToolBridge,
            ApplicationPaths,
            Apps,
            (name, arguments, correlationId) => this.operationDispatcher.CallToolAsync(name, arguments, correlationId));
        this.operationDispatcher.ConfigureRepositoryTaskRuntime(hostRuntimeOptions.JavaScriptExecutablePath);
        Health = new HostHealthService(this);
        SystemReports = new SystemReportService(hostRuntimeOptions, this);
        RemoteRunner = new RemoteRunnerStatusService();
        AppWatches = CreateAppWatches();

        operationDispatcher.ConfigureOptionalExtensions(Extensions);
        SubscribeToRuntimeEvents();
    }

    public RuntimeCoordinator(RuntimeOptions? options)
    {
        options ??= new RuntimeOptions();
        FeatureLifetime = options.FeatureLifetime;
        SessionVideoEncoder = options.SessionVideoEncoder;
        javaScriptExecutablePath = options.JavaScriptExecutablePath;
        protocolDefaultsOverride = CreateProtocolDefaultsOverride(options);

        ApplicationPaths = CreateApplicationPaths(options);
        BaseFolderPath = ApplicationPathsFactory.ResolveBaseFolderPath(ApplicationPaths);
        Analytics = new ProductAnalytics(BaseFolderPath);

        IEncryptedStorage encryptedStorage = EncryptedStorageFactory.CreateDefault(
            ApplicationPaths,
            options.SecureStorageFilePath,
            options.SecureStorageKeyFilePath);

        composition = new MefHostComposition(ApplicationPaths, encryptedStorage, options.SessionVideoEncoder);
        runtimeState = composition.Get<IRuntimeState>();
        pairingServer = composition.Get<IUdpPairingServer>();
        appToolBridge = composition.Get<IAppToolBridge>();
        operationDispatcher = composition.Get<IOperationDispatcher>();
        externalScreenshotCaptures = composition.Get<IExternalSessionScreenshotCaptureManager>();
        deviceEvidence = composition.Get<DeviceSessionEvidence>();
        UserPreferences = composition.Get<IUserPreferences>();
        SimulatorAgent = new SimulatorAgentService(encryptedStorage, operationDispatcher, ApplicationPaths);
        Ui = new UiAutomationService((name, arguments, correlationId) => operationDispatcher.CallToolAsync(name, arguments, correlationId));
        Devices = new DeviceService(options, new IosAudioRouteRecovery(ApplicationPaths.ApplicationDataPath));
        operationDispatcher.ConfigureDevicePermissions(Devices, options);
        audioInjectionEngine = new AudioInjectionEngine(runtimeState, appToolBridge, Devices, options, ApplicationPaths);
        operationDispatcher.ConfigureAudioInjection(audioInjectionEngine);
        Audio = new AudioService((name, arguments, correlationId) => operationDispatcher.CallToolAsync(name, arguments, correlationId));
        DeviceLocationPlayback = new DeviceLocationPlaybackService(Devices);
        operationDispatcher.ConfigureDeviceLocationPlayback(DeviceLocationPlayback, Devices);
        operationDispatcher.ConfigureDeviceMotionDriver(Devices);
        var hostIdentity = composition.Get<IIdentityStore>().Current;
        ExtensionStorage = encryptedStorage;
        ExtensionIdentity = hostIdentity;
        Extensions = new Ansight.Infrastructure.Extensions.OptionalExtensions(this, options.ExtensionsDirectory);
        Identity = CreateIdentityInfo(hostIdentity);
        Apps = composition.Get<AppService>();
        AppTools = new AppToolService(appToolBridge,
            id => runtimeState.TryGetSessionContext(id, out var capture) && capture?.CaptureSource == WorkspaceExecutionModes.Device);
        Pairing = composition.Get<PairingService>();
        Trends = new WorkspaceTrendsService(this, ApplicationPaths);
        Sessions = new SessionQueryService(runtimeState);
        SessionEditing = new SessionEditingService(runtimeState, runtimeState, Trends);
        headlessDeviceDriver = new HeadlessHostDeviceDriver(Devices, options);
        if (externalScreenshotCaptures is ExternalSessionScreenshotCaptureManager physicalScreenshots)
            physicalScreenshots.ConfigurePhysicalIosCapture(headlessDeviceDriver.CapturePhysicalIosScreenshotAsync);
        operationDispatcher.ConfigureUiInputDriver(headlessDeviceDriver);
        operationDispatcher.ConfigureUiAccessibilityDriver(headlessDeviceDriver);
        operationDispatcher.ConfigureDeviceLifecycleDriver(headlessDeviceDriver);
        WorkspaceTests = new WorkspaceTestService(
            this,
            SimulatorAgent,
            new WorkspaceTestTargetLauncher(Devices, Pairing, FindMonitoredSession),
            new CloudWorkspaceTestRunGateway(() => Extensions.GetService<IWorkspaceTestRunGateway>()));
        Profiling = composition.Get<DotNetProfilingService>();
        Profiling.ConfigureDeviceService(Devices);
        NativeProfiling = composition.Get<NativeProfilingService>();
        NativeProfiling.ConfigureDeviceService(Devices);
        LocalAppGraphs = new LocalAppGraphService(ApplicationPaths);
        LiveFiles = new LiveSessionFileService(runtimeState, AppTools, id => this.deviceEvidence?.RequireFiles(id),
            async (tool, arguments, token) =>
            {
                using var scope = ToolExecutionCancellation.Push(token);
                return await this.operationDispatcher.CallToolAsync(tool, arguments).ConfigureAwait(false);
            });
        FileVisualizations = new FileVisualizationService(this);
        ArtifactComparisons = new ArtifactComparisonService(this);
        SessionReplays = new SessionReplayService(this);
        SessionArchives = new SessionArchiveService(
            runtimeState,
            runtimeState,
            ApplicationPaths,
            javaScriptExecutablePath);
        SessionEvidence = new SessionEvidenceService(runtimeState, ApplicationPaths);
        SessionCache = new SessionCacheService(runtimeState, appToolBridge);
        LiveVisualTrees = new LiveVisualTreeCaptureService(runtimeState, runtimeState, appToolBridge, headlessDeviceDriver);
        RepositoryAutomations = new RepositoryAutomationService(options, appToolBridge, ApplicationPaths, Apps,
            (name, arguments, correlationId) => operationDispatcher.CallToolAsync(name, arguments, correlationId));
        operationDispatcher.ConfigureRepositoryTaskRuntime(options.JavaScriptExecutablePath);
        Health = new HostHealthService(this);
        SystemReports = new SystemReportService(options, this);
        RemoteRunner = new RemoteRunnerStatusService();
        AppWatches = CreateAppWatches();

        operationDispatcher.ConfigureOptionalExtensions(Extensions);
        SubscribeToRuntimeEvents();
    }

    public event EventHandler<AppSessionSnapshot>? SessionUpdated;

    internal string JavaScriptExecutablePath => javaScriptExecutablePath;

    public SimulatorAgentService SimulatorAgent { get; }

    public UiAutomationService Ui { get; }

    /// <summary>
    /// Creates per-connection input/evidence state for an existing connected app session.
    /// Does not open a pipe, launch an app, or create a new recording session.
    /// </summary>
    public IAppInteractionContext CreateAppInteractionContext(string sessionId, string? repositoryRootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return operationDispatcher.CreateAppInteractionContext(sessionId, repositoryRootPath);
    }

    public AudioService Audio { get; }

    public DeviceService Devices { get; }

    public DeviceLocationPlaybackService DeviceLocationPlayback { get; }

    public IdentityInfo Identity { get; }

    internal Ansight.RemoteSimulator.Core.Server.RemoteControlServer? LocalSimulatorControl { get; set; }
    internal ICompanionService? ActiveCompanion => Extensions.TryGetLoadedService<ICompanionService>();
    public ICompanionService Companion => Extensions.GetService<ICompanionService>();

    public IAccountService Account => Extensions.GetService<IAccountService>();

    public WorkspaceTestService WorkspaceTests { get; }

    public WorkspaceTrendsService Trends { get; }

    public SessionQueryService Sessions { get; }

    public SessionEditingService SessionEditing { get; }

    internal ISessionVideoEncoder? SessionVideoEncoder { get; }

    public AppService Apps { get; }

    public AppToolService AppTools { get; }

    public RepositoryAutomationService RepositoryAutomations { get; }

    public HostHealthService Health { get; }

    public SystemReportService SystemReports { get; }

    public RemoteRunnerStatusService RemoteRunner { get; }

    private IRemoteRunnerControl? ownedExtensionRunnerControl;
    public IRemoteRunnerControl? RemoteRunnerControl { get; set; }
    internal IRemoteRunnerControl? GetOrCreateRemoteRunnerControl()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return RemoteRunnerControl ??= ownedExtensionRunnerControl = RemoteRunnerControlFactory?.Invoke();
        }
    }
    public Func<IRemoteRunnerControl>? RemoteRunnerControlFactory { get; set; }

    public PairingService Pairing { get; }

    public DotNetProfilingService Profiling { get; }

    public NativeProfilingService NativeProfiling { get; }

    public ICloudService Cloud => Extensions.GetService<ICloudService>();

    public ISessionService CloudSessions => Extensions.GetService<ISessionService>();

    public LocalAppGraphService LocalAppGraphs { get; }

    internal LiveSessionFileService LiveFiles { get; }

    public FileVisualizationService FileVisualizations { get; }

    public ArtifactComparisonService ArtifactComparisons { get; }

    public SessionReplayService SessionReplays { get; }

    public SessionArchiveService SessionArchives { get; }

    public SessionEvidenceService SessionEvidence { get; }

    public SessionCacheService SessionCache { get; }

    public LiveVisualTreeCaptureService LiveVisualTrees { get; }

    private static IdentityInfo CreateIdentityInfo(RuntimeIdentity identity)
    {
        return new IdentityInfo(
            identity.HostName,
            identity.HostId,
            identity.Fingerprint,
            identity.PublicKeyBase64);
    }

    public event EventHandler<SessionLogBatchEventArgs>? SessionLogsAdded;

    public event EventHandler<string>? SessionDeleted;

    public event EventHandler? AppConnectionsChanged;

    public event EventHandler<RuntimeEvent>? EventOccurred;

    public event EventHandler<RuntimeTrendsEvent>? TrendsEventOccurred;

    public event EventHandler<RuntimeLifecycleEvent>? LifecycleEventOccurred;

    public event EventHandler<RuntimePairingEvent>? PairingEventOccurred;

    public event EventHandler<RuntimeClientAppStateChangedEvent>? ClientAppStateChanged;

    public event EventHandler<RuntimeSessionCaptureEvent>? SessionCaptureEventOccurred;

    public event EventHandler<RuntimeSessionTransferEvent>? SessionTransferEventOccurred;

    public event EventHandler<RuntimeAppEvent>? AppEventOccurred;

    /// <summary>
    /// Raised after a repository automation selected by the C# trigger engine completes.
    /// </summary>
    public event EventHandler<AutomationRunCompletedEvent>? AutomationRunCompleted;


    /// <summary>
    /// Validates and returns the visible repository tasks for one Ansight app ID.
    /// </summary>
    public RepositoryTaskCatalog InspectRepositoryTasks(
        string appId,
        string repositoryRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        var normalizedRootPath = Path.GetFullPath(repositoryRootPath.Trim());
        var normalizedAppId = appId.Trim();
        return operationDispatcher.InspectRepositoryTasks(normalizedRootPath, normalizedAppId);
    }

    /// <summary>
    /// Runs one saved repository task against an exact connected live session.
    /// </summary>
    public Task<RepositoryTaskRunResult> RunRepositoryTaskAsync(
        string repositoryRootPath,
        string appId,
        string sessionId,
        string taskId,
        JsonObject? input = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? secretValues = null)
    {
        return operationDispatcher.RunRepositoryTaskAsync(
            repositoryRootPath,
            appId,
            sessionId,
            taskId,
            input,
            cancellationToken,
            secretValues);
    }


    public void SetFocusedControl(RuntimeFocusedControlSnapshot snapshot)
    {
        runtimeState.SetFocusedControl(snapshot);
    }

    public void ClearFocusedControl(string? sessionId = null)
    {
        runtimeState.ClearFocusedControl(sessionId);
    }

    public void ConfigureUiInputDriver(IUiInputDriver? driver)
    {
        operationDispatcher.ConfigureUiInputDriver(driver);
    }

    public void ConfigureUiAccessibilityDriver(IUiAccessibilityDriver? driver)
    {
        operationDispatcher.ConfigureUiAccessibilityDriver(driver);
        LiveVisualTrees.ConfigureAccessibilityDriver(driver);
    }

    internal string? GetAppiumSessionId(
        string deviceIdentifier,
        string applicationIdentifier)
        => headlessDeviceDriver?.GetAppiumSessionId(deviceIdentifier, applicationIdentifier);

    internal void PublishTrendsEvent(RuntimeTrendsEvent trendsEvent)
        => runtimeState.PublishRuntimeEvent(trendsEvent);

    public void ConfigureDeviceLocationDriver(IDeviceLocationDriver? driver)
    {
        operationDispatcher.ConfigureDeviceLocationDriver(driver);
    }

    public void ConfigureDeviceLifecycleDriver(IDeviceLifecycleDriver? driver)
    {
        operationDispatcher.ConfigureDeviceLifecycleDriver(driver);
    }

    public RuntimeStatusSnapshot GetStatusSnapshot()
    {
        return new RuntimeStatusSnapshot(
            IsRunning,
            StartupWarnings,
            BaseFolderPath,
            DateTimeOffset.UtcNow);
    }

    public event EventHandler<RuntimeLogEntry>? LogReceived;

    public event EventHandler<string>? StatusChanged;

    public IApplicationPaths ApplicationPaths { get; }

    internal IUserPreferences UserPreferences { get; }

    public string BaseFolderPath { get; }

    internal ProductAnalytics Analytics { get; }

    public IReadOnlyList<string> StartupWarnings
    {
        get
        {
            lock (gate)
            {
                return startupWarnings.ToArray();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return started;
            }
        }
    }

    private void SubscribeToRuntimeEvents()
    {
        runtimeState.LogAdded += HandleLogAdded;
        runtimeState.RuntimeEventOccurred += HandleRuntimeEventOccurred;
        runtimeState.SessionUpdated += HandleSessionUpdated;
        runtimeState.SessionLogsAdded += HandleSessionLogsAdded;
        runtimeState.SessionDeleted += HandleSessionDeleted;
        runtimeState.ServerStatusChanged += HandlePairingServerStatusChanged;
        appToolBridge.ConnectionsChanged += HandleAppToolBridgeConnectionsChanged;
        RepositoryAutomations.RunCompleted += HandleRepositoryAutomationRunCompleted;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? currentShutdownTokenSource;
        Task? currentPairingTask;
        Task? currentAnalyticsTask;

        lock (gate)
        {
            ThrowIfDisposed();
            if (started)
            {
                return;
            }

            shutdownTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, FeatureLifetime);
            var lifetime = shutdownTokenSource.Token;
            pairingTask = Task.Run(() => RunPairingAndAnalyticsAsync(lifetime), CancellationToken.None);
            analyticsTask = Task.Run(() => new PostHogDispatcher(BaseFolderPath).RunAsync(lifetime), CancellationToken.None);
            startupWarnings = [];
            audioInjectionEngine.Start();
            started = true;

            currentShutdownTokenSource = shutdownTokenSource;
            currentPairingTask = pairingTask;
            currentAnalyticsTask = analyticsTask;
        }

        await WaitForStartupStateAsync(currentPairingTask, cancellationToken);
        var startupFailure = GetTaskFailure(currentPairingTask);
        if (startupFailure is not null)
        {
            await ResetStartedStateAsync(currentShutdownTokenSource, currentPairingTask, currentAnalyticsTask);
            throw new InvalidOperationException($"Host runtime failed during startup: {startupFailure.Message}", startupFailure);
        }

        var automationStartResult = await RepositoryAutomations.StartAsync(currentShutdownTokenSource!.Token).ConfigureAwait(false);
        await BackfillIosInstrumentsMetricsAsync(currentShutdownTokenSource.Token).ConfigureAwait(false);
        AppWatches.Start(currentShutdownTokenSource.Token);
        var currentStartupWarnings = CaptureStartupWarnings()
            .Concat(automationStartResult?.Warnings ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        StoreStartupWarnings(currentStartupWarnings);
        var startupStatus = CreateStartupStatusMessage(currentStartupWarnings);
        log.Info($"host_runtime_started status={startupStatus} warningCount={currentStartupWarnings.Length}");
        RaiseRuntimeEvent(
            new RuntimeLifecycleEvent(
                DateTimeOffset.UtcNow,
                RuntimeLifecycleEventKind.Started,
                startupStatus));
        StatusChanged?.Invoke(this, startupStatus);
    }

    private async Task RunPairingAndAnalyticsAsync(CancellationToken cancellationToken)
    {
        var usageTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await pairingServer.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Analytics.RecordUsage("host", durationSeconds: usageTimer.Elapsed.TotalSeconds);
            Analytics.Flush(force: true);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? currentPairingTask;
        Task? currentAnalyticsTask;
        CancellationTokenSource? currentShutdownTokenSource;

        lock (gate)
        {
            if (!started)
            {
                return;
            }

            started = false;
            currentPairingTask = pairingTask;
            currentAnalyticsTask = analyticsTask;
            currentShutdownTokenSource = shutdownTokenSource;
            pairingTask = null;
            analyticsTask = null;
            shutdownTokenSource = null;
        }

        await AppWatches.StopAsync().ConfigureAwait(false);
        currentShutdownTokenSource?.Cancel();
        await audioInjectionEngine.StopAsync().ConfigureAwait(false);
        await RepositoryAutomations.StopAsync().ConfigureAwait(false);
        if (deviceEvidence is not null) await deviceEvidence.StopAllAsync().ConfigureAwait(false);

        try
        {
            await WaitForTasksAsync(currentPairingTask, cancellationToken);
            if (currentAnalyticsTask is not null) await currentAnalyticsTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (currentShutdownTokenSource?.IsCancellationRequested == true)
        {
        }
        catch
        {
            await ObserveAndIgnoreTaskFailuresAsync(currentPairingTask, currentAnalyticsTask);
        }
        finally
        {
            currentShutdownTokenSource?.Dispose();
            StoreStartupWarnings(Array.Empty<string>());
            log.Info("host_runtime_stopped");
            RaiseRuntimeEvent(
                new RuntimeLifecycleEvent(
                    DateTimeOffset.UtcNow,
                    RuntimeLifecycleEventKind.Stopped,
                    "Host runtime stopped."));
            StatusChanged?.Invoke(this, "Host runtime stopped.");
        }
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task? currentPairingTask;

        lock (gate)
        {
            currentPairingTask = pairingTask;
        }

        return WaitForTasksAsync(currentPairingTask, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (started)
        {
            StopAsync().GetAwaiter().GetResult();
        }

        runtimeState.LogAdded -= HandleLogAdded;
        runtimeState.RuntimeEventOccurred -= HandleRuntimeEventOccurred;
        runtimeState.SessionUpdated -= HandleSessionUpdated;
        runtimeState.SessionLogsAdded -= HandleSessionLogsAdded;
        runtimeState.SessionDeleted -= HandleSessionDeleted;
        runtimeState.ServerStatusChanged -= HandlePairingServerStatusChanged;
        appToolBridge.ConnectionsChanged -= HandleAppToolBridgeConnectionsChanged;
        RepositoryAutomations.RunCompleted -= HandleRepositoryAutomationRunCompleted;
        RepositoryAutomations.DisposeAsync().AsTask().GetAwaiter().GetResult();

        SessionReplays.DisposeAsync().AsTask().GetAwaiter().GetResult();
        audioInjectionEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        DeviceLocationPlayback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ownedExtensionRunnerControl?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ownedExtensionRunnerControl = null;
        Extensions.Dispose();
        SimulatorAgent.Dispose();
        NativeProfiling.Dispose();
        operationDispatcher.Dispose();
        headlessDeviceDriver?.Dispose();
        protocolDefaultsOverride?.Dispose();
    }


    private static IApplicationPaths CreateApplicationPaths(RuntimeOptions options)
    {
        return string.IsNullOrWhiteSpace(options.BaseFolderPath)
            ? ApplicationPathsFactory.Create()
            : new DataToolApplicationPaths(Path.GetFullPath(options.BaseFolderPath.Trim()));
    }

    private static IDisposable? CreateProtocolDefaultsOverride(RuntimeOptions options)
    {
        var hasPortOverride = options.DiscoveryPort.HasValue
            || options.WebSocketPort.HasValue
            || options.WebSocketSessionPortRangeStart.HasValue
            || options.WebSocketSessionPortRangeEnd.HasValue;
        if (!hasPortOverride)
        {
            return null;
        }

        ValidateOptionalPort(options.DiscoveryPort, nameof(options.DiscoveryPort));
        ValidateOptionalPort(options.WebSocketPort, nameof(options.WebSocketPort));
        ValidateOptionalPort(options.WebSocketSessionPortRangeStart, nameof(options.WebSocketSessionPortRangeStart));
        ValidateOptionalPort(options.WebSocketSessionPortRangeEnd, nameof(options.WebSocketSessionPortRangeEnd));

        if (options.WebSocketSessionPortRangeStart.HasValue != options.WebSocketSessionPortRangeEnd.HasValue)
        {
            throw new ArgumentException(
                "WebSocket session port range overrides must provide both a start and an end port.",
                nameof(options));
        }

        var discoveryPort = options.DiscoveryPort ?? ProtocolDefaults.DiscoveryPort;
        var webSocketPort = options.WebSocketPort ?? ProtocolDefaults.WebSocketPort;
        var sessionRangeStart = options.WebSocketSessionPortRangeStart ?? ProtocolDefaults.WebSocketSessionPortRangeStart;
        var sessionRangeEnd = options.WebSocketSessionPortRangeEnd ?? ProtocolDefaults.WebSocketSessionPortRangeEnd;
        if (sessionRangeStart > sessionRangeEnd)
        {
            throw new ArgumentException(
                "The WebSocket session port range start must be less than or equal to its end.",
                nameof(options));
        }

        if (IsWithinRange(webSocketPort, sessionRangeStart, sessionRangeEnd))
        {
            throw new ArgumentException(
                "The WebSocket TCP port must not overlap the WebSocket session port range.",
                nameof(options));
        }

        return ProtocolDefaults.PushOverride(
            discoveryPort: discoveryPort,
            webSocketPort: webSocketPort,
            webSocketPath: ProtocolDefaults.WebSocketPath,
            webSocketSessionPortRangeStart: sessionRangeStart,
            webSocketSessionPortRangeEnd: sessionRangeEnd);
    }


    private static void ValidateOptionalPort(int? port, string parameterName)
    {
        if (port is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, port, "Ports must be between 1 and 65535.");
        }
    }

    private static bool IsWithinRange(int port, int rangeStart, int rangeEnd)
    {
        return port >= rangeStart && port <= rangeEnd;
    }

}
