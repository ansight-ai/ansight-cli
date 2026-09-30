using System.Threading.Channels;
using System.Text.Json;
using Ansight.Infrastructure.Logging;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationTriggerService : IAsyncDisposable
{
    private static readonly ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private const int MaximumRepositoryCount = 32;
    private readonly object gate = new();
    private readonly IReadOnlyList<string> repositoryPaths;
    private readonly TimeSpan defaultFunctionTimeout;
    private readonly TimeSpan defaultActionTimeout;
    private readonly int queueCapacity;
    private readonly int maximumConcurrentRuns;
    private readonly IRepositoryAutomationExecutor executor;
    private readonly string? runtimeAvailabilityWarning;
    private readonly RepositoryAutomationRunStore? runStore;
    private readonly Dictionary<RepositoryAutomationRepositoryConnectionKey, RepositoryAutomationTriggerDefinition[]> connectedRepositoryTriggers = [];
    private readonly HashSet<Task> pendingRetryTasks = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> pendingSessions = new(StringComparer.Ordinal);
    private RepositoryAutomationTriggerDefinition[] configuredRepositoryTriggers = [];
    private RepositoryAutomationCatalog catalog = RepositoryAutomationCatalog.Empty;
    private Channel<RepositoryAutomationExecutionRequest>? executionQueue;
    private CancellationTokenSource? lifetimeCts;
    private Task[] workers = [];

    public RepositoryAutomationTriggerService(
        IReadOnlyList<string> repositoryPaths,
        TimeSpan defaultFunctionTimeout,
        TimeSpan defaultActionTimeout,
        int queueCapacity,
        int maximumConcurrentRuns,
        IRepositoryAutomationExecutor executor,
        string? runtimeAvailabilityWarning = null,
        RepositoryAutomationRunStore? runStore = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);
        if (repositoryPaths.Count > MaximumRepositoryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repositoryPaths),
                repositoryPaths.Count,
                "No more than 32 automation repositories may be configured.");
        }

        this.repositoryPaths = repositoryPaths.ToArray();
        if (defaultFunctionTimeout < TimeSpan.FromMilliseconds(10)
            || defaultFunctionTimeout > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultFunctionTimeout),
                defaultFunctionTimeout,
                "The default trigger function timeout must be between 10 and 1,000 milliseconds.");
        }

        if (defaultActionTimeout <= TimeSpan.Zero || defaultActionTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultActionTimeout),
                defaultActionTimeout,
                "The default automation action timeout must be greater than zero and no more than five minutes.");
        }

        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Queue capacity must be positive.");
        }

        if (maximumConcurrentRuns <= 0 || maximumConcurrentRuns > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrentRuns),
                maximumConcurrentRuns,
                "Maximum concurrent runs must be between 1 and 16.");
        }

        this.defaultFunctionTimeout = defaultFunctionTimeout;
        this.defaultActionTimeout = defaultActionTimeout;
        this.queueCapacity = queueCapacity;
        this.maximumConcurrentRuns = maximumConcurrentRuns;
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.runtimeAvailabilityWarning = string.IsNullOrWhiteSpace(runtimeAvailabilityWarning)
            ? null
            : runtimeAvailabilityWarning.Trim();
        this.runStore = runStore;
    }

    public event EventHandler<AutomationRunCompletedEvent>? RunCompleted;

    public IReadOnlyList<RepositoryAutomationTrigger> RegisteredTriggers
    {
        get
        {
            lock (gate)
            {
                return catalog.PublicTriggers.ToArray();
            }
        }
    }

    public IReadOnlyList<AutomationRunCompletedEvent> GetRecentRuns(string appId, int limit)
        => runStore?.GetRecent(appId, limit)
           ?? Array.Empty<AutomationRunCompletedEvent>();

    public Task<RepositoryAutomationTriggerServiceStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadResult = RepositoryAutomationTriggerLoader.Load(
            repositoryPaths,
            defaultFunctionTimeout,
            defaultActionTimeout);

        lock (gate)
        {
            if (executionQueue is not null)
            {
                return Task.FromResult(new RepositoryAutomationTriggerServiceStartResult(loadResult.Warnings));
            }

            configuredRepositoryTriggers = loadResult.Catalog.Triggers.ToArray();
            RebuildCatalog();
            executionQueue = Channel.CreateBounded<RepositoryAutomationExecutionRequest>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = maximumConcurrentRuns == 1,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var currentQueue = executionQueue;
            var currentLifetimeToken = lifetimeCts.Token;
            workers = Enumerable.Range(0, maximumConcurrentRuns)
                .Select(_ => Task.Run(
                    () => ProcessQueueAsync(currentQueue.Reader, currentLifetimeToken),
                    CancellationToken.None))
                .ToArray();
        }

        return Task.FromResult(new RepositoryAutomationTriggerServiceStartResult(loadResult.Warnings));
    }

    public RepositoryAutomationRepositoryConnectionResult InspectRepository(
        string repositoryRootPath,
        string appId)
    {
        var load = LoadRepository(repositoryRootPath, appId);
        var isConnected = false;
        lock (gate)
        {
            isConnected = connectedRepositoryTriggers.ContainsKey(load.Key);
        }

        return new RepositoryAutomationRepositoryConnectionResult(
            load.IsSuccess,
            isConnected,
            load.Key.RepositoryRootPath,
            load.Key.AppId,
            load.PublicTriggers,
            AppendRuntimeAvailabilityWarning(load.Warnings));
    }

    public RepositoryAutomationRepositoryConnectionResult ConnectRepository(
        string repositoryRootPath,
        string appId)
    {
        var load = LoadRepository(repositoryRootPath, appId);
        if (!load.IsSuccess)
        {
            DisconnectRepository(load.Key.AppId);
            return new RepositoryAutomationRepositoryConnectionResult(
                false,
                false,
                load.Key.RepositoryRootPath,
                load.Key.AppId,
                load.PublicTriggers,
                AppendRuntimeAvailabilityWarning(load.Warnings));
        }

        var key = load.Key;

        lock (gate)
        {
            var replacesExistingAppConnection = connectedRepositoryTriggers.Keys.Any(existing =>
                string.Equals(existing.AppId, key.AppId, StringComparison.Ordinal));
            if (!connectedRepositoryTriggers.ContainsKey(key)
                && !replacesExistingAppConnection
                && repositoryPaths.Count + connectedRepositoryTriggers.Count >= MaximumRepositoryCount)
            {
                return new RepositoryAutomationRepositoryConnectionResult(
                    false,
                    false,
                    key.RepositoryRootPath,
                    key.AppId,
                    load.PublicTriggers,
                    AppendRuntimeAvailabilityWarning(
                        [.. load.Warnings, $"No more than {MaximumRepositoryCount} automation repositories may be connected."]));
            }

            foreach (var existingKey in connectedRepositoryTriggers.Keys
                         .Where(existing => string.Equals(existing.AppId, key.AppId, StringComparison.Ordinal)
                                            && !existing.Equals(key))
                         .ToArray())
            {
                connectedRepositoryTriggers.Remove(existingKey);
            }

            connectedRepositoryTriggers[key] = load.Triggers;
            RebuildCatalog();
        }

        return new RepositoryAutomationRepositoryConnectionResult(
            true,
            true,
            key.RepositoryRootPath,
            key.AppId,
            load.PublicTriggers,
            AppendRuntimeAvailabilityWarning(load.Warnings));
    }

    public bool DisconnectRepository(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        var removed = false;
        lock (gate)
        {
            foreach (var key in connectedRepositoryTriggers.Keys
                         .Where(existing => string.Equals(existing.AppId, normalizedAppId, StringComparison.Ordinal))
                         .ToArray())
            {
                removed |= connectedRepositoryTriggers.Remove(key);
            }

            if (removed)
            {
                RebuildCatalog();
            }
        }

        return removed;
    }

    public bool HasCandidates(string eventKind, string appId)
    {
        lock (gate)
        {
            return executionQueue is not null && catalog.HasCandidates(eventKind, appId);
        }
    }

    public void Publish(AutomationEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RepositoryAutomationCatalog currentCatalog;
        ChannelWriter<RepositoryAutomationExecutionRequest>? writer;
        lock (gate)
        {
            currentCatalog = catalog;
            writer = executionQueue?.Writer;
        }

        if (writer is null)
        {
            return;
        }

        foreach (var trigger in currentCatalog.FindMatches(envelope))
        {
            var request = new RepositoryAutomationExecutionRequest(
                Guid.CreateVersion7().ToString("N"),
                trigger,
                envelope,
                DateTimeOffset.UtcNow);
            if (envelope.SessionId is { } sessionId) pendingSessions[request.RunId] = sessionId;
            if (!writer.TryWrite(request))
            {
                PublishRejected(request, "The repository automation execution queue is full.");
            }
        }
    }

    internal async Task DrainSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (pendingSessions.Values.Contains(sessionId, StringComparer.Ordinal))
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Channel<RepositoryAutomationExecutionRequest>? queue;
        CancellationTokenSource? currentLifetimeCts;
        Task[] currentWorkers;
        Task[] currentRetryTasks;
        lock (gate)
        {
            queue = executionQueue;
            currentLifetimeCts = lifetimeCts;
            currentWorkers = workers;
            currentRetryTasks = pendingRetryTasks.ToArray();
            executionQueue = null;
            lifetimeCts = null;
            workers = [];
        }

        queue?.Writer.TryComplete();
        currentLifetimeCts?.Cancel();
        try
        {
            await Task.WhenAll(currentWorkers.Concat(currentRetryTasks)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            currentLifetimeCts?.Dispose();
            pendingSessions.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private static RepositoryAutomationRepositoryConnectionKey CreateConnectionKey(
        string repositoryRootPath,
        string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new RepositoryAutomationRepositoryConnectionKey(
            Path.GetFullPath(repositoryRootPath.Trim()),
            appId.Trim());
    }

    private RepositoryAutomationRepositoryLoad LoadRepository(
        string repositoryRootPath,
        string appId)
    {
        var key = CreateConnectionKey(repositoryRootPath, appId);
        var loadResult = RepositoryAutomationTriggerLoader.Load(
            [key.RepositoryRootPath],
            defaultFunctionTimeout,
            defaultActionTimeout,
            key.AppId);
        var triggers = loadResult.Catalog.Triggers.ToArray();
        var publicTriggers = triggers
            .Select(trigger => trigger.ToPublicDefinition())
            .OrderBy(trigger => trigger.TriggerId, StringComparer.Ordinal)
            .ToArray();
        var warnings = loadResult.Warnings.ToList();
        if (warnings.Count == 0 && triggers.Length == 0)
        {
            warnings.Add(
                $"The repository does not declare any triggers for app ID '{key.AppId}'.");
        }

        return new RepositoryAutomationRepositoryLoad(key, triggers, publicTriggers, warnings);
    }

    private void RebuildCatalog()
    {
        catalog = new RepositoryAutomationCatalog(
            configuredRepositoryTriggers
                .Concat(connectedRepositoryTriggers.Values.SelectMany(triggers => triggers))
                .DistinctBy(trigger => new RepositoryAutomationTriggerIdentity(
                    trigger.RepositoryRootPath,
                    trigger.AppId,
                    trigger.TriggerId)));
    }

    private IReadOnlyList<string> AppendRuntimeAvailabilityWarning(IReadOnlyList<string> warnings)
    {
        return runtimeAvailabilityWarning is null
            ? warnings
            : [.. warnings, runtimeAvailabilityWarning];
    }

    private async Task ProcessQueueAsync(
        ChannelReader<RepositoryAutomationExecutionRequest> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                RepositoryAutomationExecutionResult result;
                try
                {
                    result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    result = new RepositoryAutomationExecutionResult(
                        AutomationRunStatus.Failed,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        ExitCode: null,
                        $"Repository automation executor failed: {exception.Message}",
                        Output: null,
                        StandardError: string.Empty);
                }

                CompleteAttempt(request, result, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PublishRejected(RepositoryAutomationExecutionRequest request, string message)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        PublishCompleted(
            request,
            new RepositoryAutomationExecutionResult(
                AutomationRunStatus.Rejected,
                nowUtc,
                nowUtc,
                ExitCode: null,
                message,
                Output: null,
                StandardError: string.Empty),
            willRetry: false,
            nextAttemptAtUtc: null);
    }

    private void CompleteAttempt(
        RepositoryAutomationExecutionRequest request,
        RepositoryAutomationExecutionResult result,
        CancellationToken cancellationToken)
    {
        var retryPolicy = request.Trigger.Automation.RetryPolicy;
        var willRetry = ShouldRetry(result.Status)
                        && request.AttemptNumber < retryPolicy.MaximumAttempts
                        && !cancellationToken.IsCancellationRequested;
        DateTimeOffset? nextAttemptAtUtc = null;
        if (willRetry)
        {
            var retryDelay = retryPolicy.GetDelayBeforeAttempt(request.AttemptNumber + 1);
            nextAttemptAtUtc = DateTimeOffset.UtcNow + retryDelay;
        }

        PublishCompleted(request, result, willRetry, nextAttemptAtUtc);
        if (!willRetry)
        {
            return;
        }

        var retryRequest = request with
        {
            EnqueuedAtUtc = nextAttemptAtUtc!.Value,
            AttemptNumber = request.AttemptNumber + 1
        };
        ScheduleRetry(retryRequest, nextAttemptAtUtc.Value, cancellationToken);
    }

    private void ScheduleRetry(
        RepositoryAutomationExecutionRequest retryRequest,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        var retryTask = Task.Run(
            () => DelayAndEnqueueRetryAsync(retryRequest, scheduledAtUtc, cancellationToken),
            CancellationToken.None);
        lock (gate)
        {
            pendingRetryTasks.Add(retryTask);
        }

        _ = retryTask.ContinueWith(
            completedTask =>
            {
                lock (gate)
                {
                    pendingRetryTasks.Remove(completedTask);
                }

                if (completedTask.Exception is not null)
                {
                    log.Exception(completedTask.Exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DelayAndEnqueueRetryAsync(
        RepositoryAutomationExecutionRequest retryRequest,
        DateTimeOffset scheduledAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = scheduledAtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        ChannelWriter<RepositoryAutomationExecutionRequest>? writer;
        lock (gate)
        {
            writer = executionQueue?.Writer;
        }

        if (writer is null)
        {
            return;
        }

        if (writer.TryWrite(retryRequest with { EnqueuedAtUtc = DateTimeOffset.UtcNow }))
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            PublishRejected(retryRequest, "The repository automation execution queue is full while scheduling a retry.");
        }
    }

    private static bool ShouldRetry(AutomationRunStatus status)
        => status is AutomationRunStatus.Failed or AutomationRunStatus.TimedOut;

    private void PublishCompleted(
        RepositoryAutomationExecutionRequest request,
        RepositoryAutomationExecutionResult result,
        bool willRetry,
        DateTimeOffset? nextAttemptAtUtc)
    {
        if (!willRetry) pendingSessions.TryRemove(request.RunId, out _);
        try
        {
            var completedEvent = new AutomationRunCompletedEvent(
                    request.RunId,
                    request.AttemptNumber,
                    request.Trigger.Automation.RetryPolicy.MaximumAttempts,
                    willRetry,
                    nextAttemptAtUtc,
                    request.Trigger.RepositoryRootPath,
                    request.Event.AppId,
                    request.Event.SessionId,
                    request.Trigger.TriggerId,
                    request.Trigger.Automation.AutomationId,
                    request.Trigger.Automation.ActionKindName,
                    request.Trigger.Automation.ActionTarget,
                    request.Event.EventId,
                    request.Event.CorrelationId,
                    result.Status,
                    result.StartedAtUtc,
                    result.CompletedAtUtc,
                    result.ExitCode,
                    result.Message,
                    result.Output,
                    result.StandardError,
                    request.Event);
            try
            {
                runStore?.Append(completedEvent);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
            {
                log.Exception(exception);
            }

            RunCompleted?.Invoke(this, completedEvent);
        }
        catch (Exception exception)
        {
            log.Exception(exception);
        }
    }
}
