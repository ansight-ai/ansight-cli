using Ansight.Infrastructure.Logging;

namespace Ansight.Host.Runtime.Automation;

public sealed class RepositoryAutomationService : IAsyncDisposable
{
    private static readonly ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private readonly Lock connectionGate = new();
    private readonly HashSet<string> linkedAppIds = new(StringComparer.Ordinal);
    private readonly AppService appService;
    private readonly RepositoryAutomationTriggerService? triggerService;
    private readonly TimeSpan defaultFunctionTimeout;
    private readonly TimeSpan defaultActionTimeout;
    private bool started;

    internal RepositoryAutomationService(
        RuntimeOptions options,
        IAppToolBridge appToolBridge,
        IApplicationPaths applicationPaths,
        AppService appService,
        RepositoryTaskToolExecutor? hostTools = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.appService = appService ?? throw new ArgumentNullException(nameof(appService));
        defaultFunctionTimeout = options.AutomationDefaultFunctionTimeout;
        defaultActionTimeout = options.AutomationDefaultActionTimeout;
        triggerService = CreateTriggerService(options, appToolBridge, applicationPaths, hostTools);
        this.appService.Changed += HandleAppsChanged;
    }

    public event EventHandler<AutomationRunCompletedEvent>? RunCompleted
    {
        add
        {
            if (triggerService is not null)
            {
                triggerService.RunCompleted += value;
            }
        }
        remove
        {
            if (triggerService is not null)
            {
                triggerService.RunCompleted -= value;
            }
        }
    }

    internal Task DrainSessionAsync(string sessionId, CancellationToken cancellationToken)
        => triggerService?.DrainSessionAsync(sessionId, cancellationToken) ?? Task.CompletedTask;

    public IReadOnlyList<RepositoryAutomationTrigger> GetTriggers()
        => triggerService?.RegisteredTriggers ?? Array.Empty<RepositoryAutomationTrigger>();

    public RepositoryAutomationConnection Inspect(string appId, string repositoryRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        if (triggerService is not null)
        {
            return ToPublicConnection(triggerService.InspectRepository(repositoryRootPath, appId));
        }

        var normalizedRootPath = Path.GetFullPath(repositoryRootPath.Trim());
        var normalizedAppId = appId.Trim();
        var inspection = RepositoryAutomationTriggerLoader.Load(
            [normalizedRootPath],
            defaultFunctionTimeout,
            defaultActionTimeout,
            normalizedAppId);
        return new RepositoryAutomationConnection(
            inspection.Warnings.Count == 0,
            false,
            normalizedRootPath,
            normalizedAppId,
            inspection.Catalog.PublicTriggers,
            inspection.Warnings
                .Append("Repository automations are disabled for this runtime.")
                .ToArray());
    }

    public RepositoryAutomationConnection Connect(string appId, string repositoryRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        return triggerService is null
            ? Inspect(appId, repositoryRootPath)
            : ToPublicConnection(triggerService.ConnectRepository(repositoryRootPath, appId));
    }

    public bool Disconnect(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        lock (connectionGate)
        {
            linkedAppIds.Remove(normalizedAppId);
            return triggerService?.DisconnectRepository(normalizedAppId) == true;
        }
    }

    public IReadOnlyList<AutomationRunCompletedEvent> GetRecentRuns(string appId, int limit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Run trace limit must be between 1 and 500.");
        }

        return triggerService?.GetRecentRuns(appId.Trim(), limit)
               ?? Array.Empty<AutomationRunCompletedEvent>();
    }

    internal bool HasCandidates(string eventKind, string appId)
        => triggerService?.HasCandidates(eventKind, appId) == true;

    internal void Publish(AutomationEventEnvelope automationEvent)
        => triggerService?.Publish(automationEvent);

    internal async Task<RepositoryAutomationTriggerServiceStartResult?> StartAsync(
        CancellationToken cancellationToken)
    {
        started = true;
        var result = triggerService is null
            ? null
            : await triggerService.StartAsync(cancellationToken).ConfigureAwait(false);
        SynchronizeLinkedApps();
        return result;
    }

    internal async Task StopAsync()
    {
        started = false;
        if (triggerService is not null)
        {
            await triggerService.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        appService.Changed -= HandleAppsChanged;
        if (triggerService is not null)
        {
            await triggerService.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void HandleAppsChanged(object? sender, EventArgs e)
    {
        if (started)
        {
            SynchronizeLinkedApps();
        }
    }

    private void SynchronizeLinkedApps()
    {
        if (triggerService is null)
        {
            return;
        }

        var linkedApps = appService.GetDefinitions()
            .Where(static app => app.RepositoryAutomationsEnabled
                                 && !string.IsNullOrWhiteSpace(app.CodebasePath))
            .ToArray();
        var currentAppIds = linkedApps
            .Select(static app => app.AppId)
            .ToHashSet(StringComparer.Ordinal);

        lock (connectionGate)
        {
            foreach (var appId in linkedAppIds.Where(appId => !currentAppIds.Contains(appId)).ToArray())
            {
                triggerService.DisconnectRepository(appId);
                linkedAppIds.Remove(appId);
            }

            foreach (var app in linkedApps)
            {
                try
                {
                    var connection = triggerService.ConnectRepository(app.CodebasePath!, app.AppId);
                    if (connection.IsConnected)
                    {
                        linkedAppIds.Add(app.AppId);
                        continue;
                    }

                    linkedAppIds.Remove(app.AppId);
                    log.Info(
                        $"repository_automation_link_restore_failed appId={app.AppId} codebasePath={app.CodebasePath} reason={string.Join("; ", connection.Warnings)}");
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or ArgumentException
                                                   or NotSupportedException)
                {
                    linkedAppIds.Remove(app.AppId);
                    log.Info(
                        $"repository_automation_link_restore_failed appId={app.AppId} codebasePath={app.CodebasePath} reason={exception.Message}");
                }
            }
        }
    }

    private static RepositoryAutomationTriggerService? CreateTriggerService(
        RuntimeOptions options,
        IAppToolBridge appToolBridge,
        IApplicationPaths applicationPaths,
        RepositoryTaskToolExecutor? hostTools)
    {
        if (!options.EnableRepositoryAutomations)
        {
            return null;
        }

        var appToolExecutor = new AppToolRepositoryAutomationExecutor(appToolBridge);
        var javaScriptRuntime = JavaScriptRuntimeResolver.Resolve(options.JavaScriptExecutablePath);
        log.Info(
            javaScriptRuntime.IsAvailable
                ? $"repository_automation_javascript_runtime_resolved path={javaScriptRuntime.ExecutablePath}"
                : $"repository_automation_javascript_runtime_unavailable path={javaScriptRuntime.ExecutablePath}");

        return new RepositoryAutomationTriggerService(
            options.AutomationRepositoryPaths,
            options.AutomationDefaultFunctionTimeout,
            options.AutomationDefaultActionTimeout,
            options.AutomationQueueCapacity,
            options.AutomationMaxConcurrentRuns,
            new DispatchingRepositoryAutomationExecutor(
                new JavaScriptRepositoryAutomationExecutor(
                    javaScriptRuntime.ExecutablePath,
                    appToolExecutor,
                    javaScriptRuntime.IsAvailable ? null : javaScriptRuntime.Message, hostTools),
                appToolExecutor),
            javaScriptRuntime.IsAvailable ? null : javaScriptRuntime.Message,
            new RepositoryAutomationRunStore(applicationPaths.ApplicationDataPath));
    }

    private static RepositoryAutomationConnection ToPublicConnection(
        RepositoryAutomationRepositoryConnectionResult result)
        => new(
            result.IsSuccess,
            result.IsConnected,
            result.RepositoryRootPath,
            result.AppId,
            result.Triggers,
            result.Warnings);
}
