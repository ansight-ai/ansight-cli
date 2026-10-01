using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.SimulatorAgent;

public sealed class SimulatorAgentService : IDisposable
{
    private const string LegacyLocalModelCredentialStorageKey = "simulator-agent.openai-api-key.v1";
    private const string CompleteInstructionToolName = "complete_instruction";
    private const string ReportAppGraphProgressToolName = "report_app_graph_progress";
    private const string DeclareUncoveredStepToolName = "ansight_declare_uncovered_step";
    private const int MaximumInstructionCount = 50;
    internal const int MaximumInstructionCharacters = 4_000;
    private const int MaximumTurnsPerInstructionLimit = 1_024;
    private const int MaximumRoundTripsLimit = 1_024;
    private const int MaximumToolCallsLimit = 4_000;
    private const int MaximumInstructionCharactersLimit = 256_000;
    private const int MaximumHistoryItems = 120;
    private const int MaximumSupersededToolMessageCharacters = 400;
    private const int DefaultMaximumModelOutputTokens = 2_400;
    private const int MaximumModelOutputTokensLimit = 64_000;
    private const int MaximumConsecutiveReadOnlyToolCalls = 8;
    private const int MaximumAppGraphStableActionAttemptsPerScope = 2;
    private const int MaximumAppGraphScrollAttemptsPerScope = 2;
    private const int MaximumAuditArgumentCharacters = 32_000;
    private const int MaximumAuditResultCharacters = 256_000;
    private const int MaximumAuditAssistantTextCharacters = 32_000;
    private const int MaximumAuditModelContextCharacters = 1_000_000;
    private const int MaximumAuditOcrResultCharacters = 1_000_000;
    private const int MaximumAppGraphGuidanceCharacters = 16_000;
    private const int AuditSchemaVersion = 16;
    private const string PromptCacheKey = "ansight-simulator-agent-v35";
    private static readonly HashSet<string> strongTaskMatchIgnoredWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "for", "from", "in", "into", "it", "its", "no", "of", "on", "or",
        "s", "that", "the", "then", "this", "to", "with"
    };
    private static readonly string[][] strongTaskSynonymGroups =
    [
        ["open", "launch", "enter", "show", "view"],
        ["verify", "validate", "check", "confirm", "assert"],
        ["search", "find", "locate", "lookup"],
        ["select", "choose", "pick", "tap"],
        ["guide", "viewer", "tour", "walkthrough"]
    ];

    private readonly SecretStore secretStore;
    private readonly IOpenAiClient openAiClient;
    private readonly IToolGateway toolGateway;
    private readonly IAuditStore auditStore;
    private readonly AppGraphLiveRunStore appGraphLiveRuns;
    private readonly IOpenAiSessionFactory? openAiWebSocketSessionFactory;
    private string? defaultModelAccessTokenForTesting;
    private bool disposed;

    internal SimulatorAgentService(
        IEncryptedStorage encryptedStorage,
        IOperationDispatcher operations,
        IApplicationPaths applicationPaths)
        : this(
            encryptedStorage,
            new OpenAiResponsesClient(),
            new ToolGateway(operations),
            new AuditStore(applicationPaths),
            new AppGraphExplorationDatabase(applicationPaths),
            new OpenAiResponsesWebSocketSessionFactory())
    {
    }

    internal SimulatorAgentService(
        IEncryptedStorage encryptedStorage,
        IOpenAiClient openAiClient,
        IToolGateway toolGateway,
        IAuditStore? auditStore = null,
        IAppGraphExplorationDatabase? appGraphDatabase = null,
        IOpenAiSessionFactory? openAiWebSocketSessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(encryptedStorage);
        encryptedStorage.Remove(LegacyLocalModelCredentialStorageKey);
        secretStore = new SecretStore(encryptedStorage);
        this.openAiClient = openAiClient ?? throw new ArgumentNullException(nameof(openAiClient));
        this.toolGateway = toolGateway ?? throw new ArgumentNullException(nameof(toolGateway));
        this.auditStore = auditStore ?? NullAuditStore.Instance;
        appGraphLiveRuns = new AppGraphLiveRunStore(appGraphDatabase);
        this.openAiWebSocketSessionFactory = openAiWebSocketSessionFactory;
    }

    internal void SetDefaultModelAccessTokenForTesting(string accessToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        defaultModelAccessTokenForTesting = accessToken.Trim();
    }

    public IReadOnlyList<SimulatorAgentRunHistoryEntry> ListRunHistory()
    {
        ThrowIfDisposed();
        return auditStore.List();
    }

    public SimulatorAgentAuditPersistenceResult PersistRunAudit(SimulatorAgentRunAudit audit)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            var result = auditStore.Save(audit);
            return new SimulatorAgentAuditPersistenceResult(result.FilePath, result.ErrorMessage);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidOperationException)
        {
            return new SimulatorAgentAuditPersistenceResult(null, exception.Message);
        }
    }

    public IReadOnlyList<SimulatorAgentAppGraphLiveRun> ListAppGraphLiveRuns(string? sessionId = null)
    {
        ThrowIfDisposed();
        return appGraphLiveRuns.List(sessionId);
    }

    public SimulatorAgentAppGraphLiveRun? GetAppGraphLiveRun(string runId)
    {
        ThrowIfDisposed();
        return appGraphLiveRuns.Get(runId);
    }

    public void MarkAppGraphLiveRunFailed(string runId, string message)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        appGraphLiveRuns.Complete(runId.Trim(), "failed", message.Trim(), DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<SimulatorAgentSecretMetadata> ListTestSecrets(string appId)
    {
        ThrowIfDisposed();
        return secretStore.List(appId);
    }

    public SimulatorAgentSecretMetadata SetTestSecret(string appId, string alias, string value)
    {
        ThrowIfDisposed();
        return secretStore.Set(appId, alias, value);
    }

    public bool RemoveTestSecret(string appId, string alias)
    {
        ThrowIfDisposed();
        return secretStore.Remove(appId, alias);
    }

    public SimulatorAgentSecretMetadata? GetTestSecretMetadata(
        string appId,
        string alias,
        Func<string, string?>? secretResolver = null)
    {
        ThrowIfDisposed();
        return secretStore.GetMetadata(appId, alias, secretResolver);
    }

    public async Task<SimulatorAgentRunResult> RunAsync(
        SimulatorAgentRunRequest request,
        IProgress<SimulatorAgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = NormalizeRequired(request.SessionId, nameof(request.SessionId));
        var appId = string.IsNullOrWhiteSpace(request.AppId) ? null : request.AppId.Trim();
        var appGraphExplorationName = NormalizeOptional(request.AppGraphExplorationName);
        if (appGraphExplorationName?.Length > 300)
        {
            throw new ArgumentException(
                "The App Graph exploration name must be 300 characters or fewer.",
                nameof(request));
        }
        var secretAccess = secretStore.CreateRunAccess(
            appId,
            request.SecretAliases,
            request.SecretResolver);
        var model = NormalizeRequired(request.Model, nameof(request.Model));
        var reasoning = AgentReasoningModes.Normalize(request.Reasoning);
        var reasoningEffort = AgentReasoningConfiguration.NormalizeReasoningEffort(request.ReasoningEffort);
        var instructions = NormalizeInstructions(
            request.Instructions,
            request.MaximumInstructionCharacters);
        var hasUsableAppGraphRoute = request.AppGraphPlans.Any(HasUsableAppGraphPlan);
        var appGraphContextEnabled = hasUsableAppGraphRoute || appGraphExplorationName is not null;
        var sessionCapabilities = SessionCapabilities.Empty();
        var agentInstructions = BuildAgentInstructions(
            hasUsableAppGraphRoute,
            appGraphExplorationName is not null,
            sessionCapabilities);
        var maximumModelOutputTokens = request.MaximumModelOutputTokens
                                       ?? DefaultMaximumModelOutputTokens;
        ValidateBudgets(
            request.MaximumTurnsPerInstruction,
            request.MaximumRoundTrips,
            request.MaximumToolCalls,
            maximumModelOutputTokens);
        var modelTransport = request.ModelTransport;
        var credentialStartedUtc = DateTimeOffset.UtcNow;
        var credentialTimer = Stopwatch.StartNew();
        var startupSteps = new System.Collections.Concurrent.ConcurrentQueue<SimulatorAgentStartupStep>(request.StartupSteps);
        var apiKey = modelTransport is null
            ? defaultModelAccessTokenForTesting
            : await modelTransport.ResolveAccessKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Agent execution requires an Ansight brokered model transport. Sign in with 'ansight account login'.");
        startupSteps.Enqueue(new("Resolve run credentials", credentialStartedUtc, credentialTimer.ElapsedMilliseconds, "succeeded"));
        if (request.TrackingRunId == Guid.Empty) throw new ArgumentException("The test tracking run ID cannot be empty.", nameof(request));
        var supportsWebSockets = modelTransport?.SupportsWebSockets ?? true;
        if (request.OpenAiProtocol == SimulatorAgentOpenAiProtocol.WebSocket && (!supportsWebSockets || openAiWebSocketSessionFactory is null))
            throw new ArgumentException("WebSocket mode is unavailable for the selected transport.", nameof(request));
        var useWebSocket = supportsWebSockets && request.OpenAiProtocol != SimulatorAgentOpenAiProtocol.Http && openAiWebSocketSessionFactory is not null;
        await using var openAiWebSocketSession = useWebSocket ? openAiWebSocketSessionFactory!.Create() : null;
        var webSocketActive = openAiWebSocketSession is not null;
        var openAiProtocol = webSocketActive ? "websocket" : "http";
        var webSocketOptions = webSocketActive ? SimulatorAgentWebSocketOptions.Resolve(request.WebSocketOptions, Environment.GetEnvironmentVariable) : null;

        var runId = (request.TrackingRunId ?? Guid.NewGuid()).ToString("N");
        using var taskTraceScope = RepositoryTaskTraceScope.Begin(request.CaptureTrace);
        toolGateway.BeginRun(sessionId, secretAccess, request.TargetDeviceIdentifier);
        var startedUtc = DateTimeOffset.UtcNow;
        if (appGraphExplorationName is not null)
        {
            appGraphLiveRuns.Begin(runId, sessionId, appId, appGraphExplorationName, startedUtc);
        }
        var stopwatch = Stopwatch.StartNew();
        var results = new List<SimulatorAgentInstructionResult>();
        var modelPassAudits = new List<SimulatorAgentModelPassAudit>();
        var toolCallAudits = new List<SimulatorAgentToolCallAudit>();
        var repositoryTaskDiscovery = new List<SimulatorAgentRepositoryTaskDiscoveryTrace>();
        var totalTurns = 0;
        var totalToolCalls = 0;
        var totalCompletionGracePasses = 0;
        var tokenUsage = SimulatorAgentTokenUsage.Empty;
        var warmupResponses = new Dictionary<string, SimulatorAgentModelPassUsage>(StringComparer.Ordinal);
        var activeInstructionIndex = 0;
        var activeInstructionTurn = 0;
        var activeInstructionToolCalls = 0;
        ToolSessionContext? sessionContext = null;

        var setupStartedUtc = DateTimeOffset.UtcNow;
        var setupTimer = Stopwatch.StartNew();
        void RecordSetupStep(string name, string status = "succeeded")
        {
            if (activeInstructionIndex > 1) return;
            startupSteps.Enqueue(new(name, setupStartedUtc, setupTimer.ElapsedMilliseconds, status));
            setupStartedUtc = DateTimeOffset.UtcNow;
            setupTimer.Restart();
        }

        async Task MeasureWarmupAsync(Func<Task> warmup)
        {
            var started = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            var status = "succeeded";
            try
            {
                await warmup().ConfigureAwait(false);
                if (openAiWebSocketSession?.LastTransportDiagnostics?.Attempts.Any(attempt => attempt.Error is not null) == true)
                    status = "failed";
            }
            catch (OperationCanceledException) { status = cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out"; throw; }
            catch { status = "failed"; throw; }
            finally { startupSteps.Enqueue(new("WebSocket warmup", started, timer.ElapsedMilliseconds, status)); }
        }

        void RecordWarmupUsage(SimulatorAgentTransportDiagnostics? transport)
        {
            foreach (var usage in transport?.WarmupResponses ?? [])
            {
                if (warmupResponses.TryAdd(usage.ResponseId, usage))
                {
                    tokenUsage = tokenUsage.Add(usage.Tokens);
                }
            }
        }

        void ReportProgress(SimulatorAgentProgress update)
        {
            progress?.Report(update);
            if (appGraphExplorationName is not null)
            {
                appGraphLiveRuns.AppendTrace(
                    runId,
                    update.Stage,
                    update.Message,
                    update.Turn,
                    update.ToolName);
            }
        }

        SimulatorAgentRunResult Finish(SimulatorAgentRunStatus status, string message)
        {
            stopwatch.Stop();
            var completedUtc = DateTimeOffset.UtcNow;
            if (appGraphExplorationName is not null)
            {
                appGraphLiveRuns.Complete(
                    runId,
                    status switch
                    {
                        SimulatorAgentRunStatus.Succeeded => "succeeded",
                        SimulatorAgentRunStatus.Cancelled => "cancelled",
                        _ => "failed"
                    },
                    message,
                    completedUtc);
            }
            var audit = new SimulatorAgentRunAudit(
                AuditSchemaVersion,
                runId,
                sessionId,
                model,
                status,
                message,
                startedUtc,
                completedUtc,
                stopwatch.ElapsedMilliseconds,
                request.MaximumTurnsPerInstruction,
                request.MaximumToolCalls,
                instructions.Count,
                results.Count(static result => result.Status == SimulatorAgentInstructionStatus.Succeeded),
                results.Count(static result => result.Status == SimulatorAgentInstructionStatus.Failed),
                results.Count(static result => result.Status == SimulatorAgentInstructionStatus.Cancelled),
                modelPassAudits.Count,
                modelPassAudits.Count(static pass => !pass.Succeeded),
                toolCallAudits.Count,
                totalToolCalls,
                toolCallAudits.Count(static call => call.IsAnsightTool && !call.IsError),
                toolCallAudits.Count(static call => call.IsAnsightTool && call.IsError),
                tokenUsage,
                instructions.ToArray(),
                results.ToArray(),
                modelPassAudits.ToArray(),
                toolCallAudits.ToArray())
            {
                AppId = appId,
                ExecutionMode = sessionCapabilities.IsDeviceOnly ? WorkspaceExecutionModes.Device : WorkspaceExecutionModes.Sdk,
                Reasoning = reasoning,
                ReasoningEffort = reasoningEffort,
                ReasoningConfigurationRevision = request.ReasoningConfigurationRevision,
                ContinueAfterInstructionFailure = request.ContinueAfterInstructionFailure,
                AgentPrompt = request.CaptureTrace ? agentInstructions : null,
                PromptCacheKey = PromptCacheKey,
                MaximumModelOutputTokens = maximumModelOutputTokens,
                CompletionGracePassCount = totalCompletionGracePasses,
                OpenAiTransport = modelTransport?.Description ?? "direct",
                OpenAiProtocol = openAiProtocol,
                StartupSteps = startupSteps.ToArray(),
                WebSocketOptions = webSocketOptions,
                WarmupResponses = warmupResponses.Values.ToArray(),
                SecretReferences = secretAccess.Secrets.Values
                    .OrderBy(static secret => secret.Alias, StringComparer.OrdinalIgnoreCase)
                    .Select(static secret => new SimulatorAgentSecretReference(
                        secret.Alias,
                        secret.VersionId))
                    .ToArray(),
                ReplayedFromRunId = string.IsNullOrWhiteSpace(request.ReplayedFromRunId)
                    ? null
                    : request.ReplayedFromRunId.Trim(),
                MaximumRoundTrips = request.MaximumRoundTrips,
                WorkspacePath = NormalizeOptional(request.WorkspacePath),
                WorkspaceTestId = NormalizeOptional(request.WorkspaceTestId),
                WorkspaceTestName = NormalizeOptional(request.WorkspaceTestName),
                BatchRunId = NormalizeOptional(request.BatchRunId),
                AppGraphPlans = request.CaptureTrace ? request.AppGraphPlans.ToArray() : [],
                AppGraphEnabled = request.AppGraphEnabled || request.AppGraphPlans.Any(HasUsableAppGraphPlan),
                TraceEnabled = request.CaptureTrace,
                RepositoryTaskDiscovery = repositoryTaskDiscovery.ToArray(),
                Environment = sessionContext is null
                    ? null
                    : new SimulatorAgentRunEnvironment(
                        sessionContext.AppId,
                        sessionContext.AppName,
                        sessionContext.IsLive,
                        sessionContext.AppState,
                        sessionContext.Device)
            };

            AuditSaveResult saveResult;
            try
            {
                saveResult = auditStore.Save(audit);
            }
            catch (Exception exception)
            {
                saveResult = new AuditSaveResult(null, exception.Message);
            }
            toolGateway.EndRun();

            var completionMessage = saveResult.FilePath is null
                ? message
                : $"{message} Audit saved to {saveResult.FilePath}.";
            ReportProgress(new SimulatorAgentProgress(
                SimulatorAgentProgressStage.Completed,
                completionMessage,
                Math.Min(Math.Max(activeInstructionIndex, results.Count), instructions.Count),
                instructions.Count,
                totalTurns));
            var result = BuildResult(
                status,
                message,
                results,
                totalTurns,
                totalToolCalls,
                tokenUsage,
                stopwatch.Elapsed,
                audit,
                saveResult);
            return result;
        }

        ReportProgress(new SimulatorAgentProgress(
            SimulatorAgentProgressStage.Starting,
            $"Starting audited run {runId} with {instructions.Count} instruction(s) against the selected app target.",
            1,
            instructions.Count,
            0));

        try
        {
            var completionOnlyTools = new JsonArray(BuildCompleteInstructionToolDefinition());
            sessionContext = await toolGateway.GetSessionContextAsync(sessionId, cancellationToken);
            RecordSetupStep("Resolve session context");
            sessionCapabilities = await toolGateway.GetSessionCapabilitiesAsync(
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            RecordSetupStep("Discover session capabilities");
            var useDeferredToolLoading = webSocketActive
                && !sessionCapabilities.IsDeviceOnly
                && OpenAiResponsesClient.SupportsPromptCacheBreakpoints(model)
                && !appGraphContextEnabled;
            agentInstructions = BuildAgentInstructions(
                hasUsableAppGraphRoute,
                appGraphExplorationName is not null,
                sessionCapabilities,
                includeToolGuidance: !useDeferredToolLoading);
            var knownSuccessfulAppToolCalls = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(sessionContext?.AppId))
            {
                appId = sessionContext.AppId.Trim();
            }

            for (var instructionIndex = 0; instructionIndex < instructions.Count; instructionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                activeInstructionIndex = instructionIndex + 1;
                activeInstructionTurn = 0;
                activeInstructionToolCalls = 0;
                var instruction = instructions[instructionIndex];
                var instructionToolCalls = 0;
                RecordSetupStep("Build agent instructions");
                var repositoryTasks = appGraphExplorationName is null
                    ? await toolGateway.GetRepositoryTaskShortcutsAsync(
                            sessionId,
                            instruction,
                            cancellationToken,
                            request.CaptureTrace
                                ? trace =>
                                {
                                    var instructionTrace = trace with { InstructionIndex = instructionIndex + 1 };
                                    repositoryTaskDiscovery.Add(instructionTrace);
                                    ReportProgress(new SimulatorAgentProgress(
                                        SimulatorAgentProgressStage.TaskDiscovery,
                                        trace.Stage == "selection"
                                            ? $"Selected {trace.SelectedTaskIds.Count} repository task shortcut(s)."
                                            : $"Searched repository tasks: {trace.Query}",
                                        instructionIndex + 1,
                                        instructions.Count,
                                        0)
                                    {
                                        TaskDiscovery = instructionTrace
                                    });
                                }
                                : null)
                        .ConfigureAwait(false)
                    : [];
                RecordSetupStep($"Discover repository tasks (instruction {instructionIndex + 1})");
                var tools = toolGateway.BuildOpenAiToolDefinitions(
                    repositoryTasks,
                    sessionCapabilities,
                    appGraphContextEnabled);
                if (appGraphExplorationName is not null && !sessionCapabilities.IsDeviceOnly)
                    RemoveScreenScanTool(tools);
                RemoveIrrelevantLifecycleTools(tools, sessionContext, instruction);
                if (appGraphExplorationName is not null)
                {
                    RemoveUnsupportedAppGraphTools(tools);
                    tools.Add(BuildReportAppGraphProgressToolDefinition());
                }
                if (repositoryTasks.Count > 0)
                {
                    tools.Add(BuildDeclareUncoveredStepToolDefinition(repositoryTasks));
                }
                tools.Add(BuildCompleteInstructionToolDefinition());
                var toolCatalog = useDeferredToolLoading ? new AgentToolCatalog(tools) : null;
                if (toolCatalog is not null)
                {
                    tools = toolCatalog.InitialTools;
                }
                RecordSetupStep("Build tool definitions");
                var shouldWarmup = webSocketActive && webSocketOptions!.EnableWarmup && totalTurns < request.MaximumRoundTrips;
                if (shouldWarmup && modelTransport is not null)
                {
                    // Issuance belongs to the run; the optional warmup timeout must never cancel a mint.
                    apiKey = (await modelTransport.ResolveAccessKeyAsync(cancellationToken).ConfigureAwait(false));
                }
                RecordSetupStep("Refresh credentials before warmup", shouldWarmup && modelTransport is not null ? "succeeded" : "skipped");
                using var warmupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                warmupCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                var warmupTask = shouldWarmup
                    ? MeasureWarmupAsync(() => openAiWebSocketSession!.WarmupAsync(
                        new OpenAiRequest(
                            apiKey, model, agentInstructions, [], tools, reasoningEffort,
                            PromptCacheKey, maximumModelOutputTokens)
                        {
                            Transport = modelTransport,
                            CompactThresholdTokens = webSocketOptions!.CompactThresholdTokens,
                            UsePromptCacheBreakpoint = OpenAiResponsesClient.SupportsPromptCacheBreakpoints(model)
                        },
                        warmupCancellation.Token))
                    : Task.CompletedTask;
                if (repositoryTasks.Count > 0)
                {
                    ReportProgress(new SimulatorAgentProgress(
                        SimulatorAgentProgressStage.Starting,
                        $"Preloaded {repositoryTasks.Count} relevant repository task shortcut(s) for instruction {instructionIndex + 1}: {string.Join(", ", repositoryTasks.Select(task => task.TaskId))}.",
                        instructionIndex + 1,
                        instructions.Count,
                        0));
                }
                ToolCallResult? initialObservation = null;
                if (sessionContext is { IsLive: true }
                    && (sessionCapabilities.IsDeviceOnly || string.Equals(sessionContext.AppState, "foreground", StringComparison.OrdinalIgnoreCase))
                    && totalToolCalls < request.MaximumToolCalls)
                {
                    var observationStartedUtc = DateTimeOffset.UtcNow;
                    var observationStopwatch = Stopwatch.StartNew();
                    var observationCorrelationId = $"{runId}-initial-observation-{instructionIndex + 1}";
                    try
                    {
                        initialObservation = await toolGateway.CaptureInitialObservationAsync(
                            sessionId, sessionCapabilities, observationCorrelationId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        initialObservation = new ToolCallResult(true,
                            new JsonObject { ["isError"] = true,
                                ["message"] = "Initial current-page capture unavailable; observe before acting." }.ToJsonString(),
                            exception.Message);
                    }
                    if (initialObservation is not null)
                    {
                        totalToolCalls++;
                        instructionToolCalls++;
                        activeInstructionToolCalls = instructionToolCalls;
                        toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                            toolCallAudits.Count + 1, instructionIndex + 1, 0,
                            observationCorrelationId, "ansight_get_live_visual_tree", true,
                            observationCorrelationId, observationStartedUtc, observationStopwatch.ElapsedMilliseconds,
                            CreateAuditPayload(sessionCapabilities.CreateInitialObservationArguments().ToJsonString(), MaximumAuditArgumentCharacters, request.CaptureTrace),
                            CreateAuditPayload(initialObservation.Output, MaximumAuditResultCharacters, request.CaptureTrace),
                            initialObservation.IsError, initialObservation.Message)
                        {
                            AccessibilityEvidence = CreateAccessibilityTraceEvidence(
                                initialObservation.AccessibilityEvidence, request.CaptureTrace)
                        });
                    }
                }
                RecordSetupStep("Capture initial UI observation", initialObservation is null ? "skipped" : initialObservation.IsError ? "failed" : "succeeded");
                var initialInput = BuildInitialInput(
                    instruction,
                    instructionIndex,
                    instructions.Count,
                    results,
                    sessionContext,
                    repositoryTasks,
                    knownSuccessfulAppToolCalls,
                    request.AppGraphPlans,
                    appGraphExplorationName is null ? null : appGraphLiveRuns.Get(runId));
                if (initialObservation is not null)
                {
                    initialInput.Add(CreateUserInput("Host-provided current-page observation (captured before this instruction):\n"
                        + (initialObservation.ModelOutput ?? initialObservation.Output)));
                }
                if (toolCatalog is { InitialAdditionalTools.Count: > 0 })
                {
                    initialInput.Insert(0, AgentToolCatalog.CreateAdditionalToolsInput(toolCatalog.InitialAdditionalTools));
                }
                var conversation = new OpenAiSimulatorAgentConversation(initialInput,
                    appendOnly: webSocketActive,
                    initialReplayReason: instructionIndex == 0 ? "initial" : "instruction-boundary");
                var navigationGuidance = new AgentNavigationGuidance();
                if (initialObservation is not null)
                {
                    conversation.AddGuidance(navigationGuidance.TakeNewGuidance(
                        initialObservation.ModelOutput ?? initialObservation.Output));
                }
                var history = conversation.History;
                var instructionCompleted = false;
                string? lastToolFailure = null;
                var repeatedToolFailures = new Dictionary<string, int>(StringComparer.Ordinal);
                var consecutiveReadOnlyToolCalls = 0;
                var readOnlyStagnationWarningIssued = false;
                var appGraphScrollAttemptsByScope = new Dictionary<string, int>(StringComparer.Ordinal);
                var appGraphTerminalScrollScopes = new HashSet<string>(StringComparer.Ordinal);
                string? appGraphCurrentDestinationId = null;
                string? stagnationSummary = null;
                string? lastCameraStateSignature = null;
                var awaitingPostSwipeCameraState = false;
                var cameraChangeVerified = false;
                string? lastTypedText = null;
                var attemptedRepositoryTaskIds = new HashSet<string>(StringComparer.Ordinal);
                var successfulRepositoryTaskIds = new HashSet<string>(StringComparer.Ordinal);
                var failedRepositoryTaskIds = new HashSet<string>(StringComparer.Ordinal);
                var excludedRepositoryTaskIds = new HashSet<string>(StringComparer.Ordinal);
                var taskReassessmentRequired = repositoryTasks.Count > 0;
                var lastRepositoryTaskState = BuildRepositoryTaskState(
                    repositoryTasks, attemptedRepositoryTaskIds, failedRepositoryTaskIds,
                    excludedRepositoryTaskIds, taskReassessmentRequired);
                var allowCompletionGracePass = false;
                var completionGracePassUsed = false;
                var roundTripBudgetExhausted = false;

                RecordSetupStep("Build initial model context");
                try
                {
                    await warmupTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Warmup is optional. Its bounded timeout must not prevent generation.
                }
                finally
                {
                    RecordWarmupUsage(openAiWebSocketSession?.LastTransportDiagnostics);
                }

                RecordSetupStep("Wait for overlapping warmup", shouldWarmup ? "succeeded" : "skipped");
                for (var turn = 1;
                     turn <= request.MaximumTurnsPerInstruction + (allowCompletionGracePass ? 1 : 0);
                     turn++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalTurns >= request.MaximumRoundTrips)
                    {
                        roundTripBudgetExhausted = true;
                        break;
                    }

                    var isCompletionGracePass = turn > request.MaximumTurnsPerInstruction;
                    var turnHadSuccessfulToolCall = false;
                    if (isCompletionGracePass)
                    {
                        completionGracePassUsed = true;
                        totalCompletionGracePasses++;
                        conversation.AddUserInput(RenderFeedbackPrompt(
                                "completion-grace",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["MAXIMUM_TURNS"] = request.MaximumTurnsPerInstruction.ToString(),
                                    ["COMPLETION_TOOL"] = CompleteInstructionToolName
                                }));
                    }

                    activeInstructionTurn = turn;
                    totalTurns++;
                    ReportProgress(new SimulatorAgentProgress(
                        SimulatorAgentProgressStage.Thinking,
                        isCompletionGracePass
                            ? $"Starting completion-only model pass {totalTurns} for instruction {instructionIndex + 1}."
                            : $"Starting model pass {totalTurns}: instruction {instructionIndex + 1}, turn {turn}.",
                        instructionIndex + 1,
                        instructions.Count,
                        turn));
                    if (appGraphExplorationName is not null)
                    {
                        appGraphLiveRuns.UpdateActivity(
                            runId,
                            $"Exploring {appGraphExplorationName}: model turn {turn}.",
                            turn);
                    }

                    var modelPassStartedUtc = DateTimeOffset.UtcNow;
                    var modelPassStopwatch = Stopwatch.StartNew();
                    var availableTools = isCompletionGracePass && !webSocketActive ? completionOnlyTools : tools;
                    SimulatorAgentAuditPayload? modelContextAudit = null;
                    if (request.CaptureTrace)
                    {
                        modelContextAudit = CreateAuditPayload(
                            new JsonObject
                            {
                                ["instructions"] = agentInstructions,
                                ["input"] = history.DeepClone(),
                                ["tools"] = availableTools.DeepClone()
                            }.ToJsonString(),
                            MaximumAuditModelContextCharacters);
                    }
                    OpenAiTurn response;
                    var attemptedWebSocket = webSocketActive;
                    try
                    {
                        if (modelTransport is not null)
                        {
                            apiKey = (await modelTransport.ResolveAccessKeyAsync(cancellationToken).ConfigureAwait(false));
                        }
                        var openAiRequest = new OpenAiRequest(
                                apiKey,
                                model,
                                agentInstructions,
                                history,
                                availableTools,
                                reasoningEffort,
                                PromptCacheKey,
                                maximumModelOutputTokens)
                        {
                            Transport = modelTransport,
                            IncrementalInput = conversation.PendingInput,
                            StartNewConversation = conversation.RequiresFullReplay,
                            ReplayReason = conversation.ReplayReason,
                            CompactThresholdTokens = webSocketActive ? webSocketOptions!.CompactThresholdTokens : null,
                            UsePromptCacheBreakpoint = webSocketActive && OpenAiResponsesClient.SupportsPromptCacheBreakpoints(model),
                            CompletionOnly = isCompletionGracePass
                        };
                        if (webSocketActive && openAiWebSocketSession is not null)
                        {
                            try
                            {
                                response = await openAiWebSocketSession.CreateResponseAsync(
                                    openAiRequest,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (OpenAiWebSocketTransportException)
                                when (request.OpenAiProtocol == SimulatorAgentOpenAiProtocol.Auto)
                            {
                                webSocketActive = false;
                                openAiProtocol = "http-fallback";
                                useDeferredToolLoading = false;
                                if (toolCatalog is not null)
                                {
                                    tools = toolCatalog.FullTools;
                                    toolCatalog = null;
                                    agentInstructions = BuildAgentInstructions(hasUsableAppGraphRoute,
                                        appGraphExplorationName is not null, sessionCapabilities);
                                }
                                conversation.UseHttpHistory();
                                if (request.CaptureTrace)
                                {
                                    modelContextAudit = CreateAuditPayload(new JsonObject
                                    {
                                        ["instructions"] = agentInstructions,
                                        ["input"] = history.DeepClone(),
                                        ["tools"] = (isCompletionGracePass ? completionOnlyTools : tools).DeepClone()
                                    }.ToJsonString(), MaximumAuditModelContextCharacters);
                                }
                                response = await openAiClient.CreateResponseAsync(
                                    openAiRequest with
                                    {
                                        Tools = isCompletionGracePass ? completionOnlyTools : tools,
                                        Instructions = agentInstructions,
                                        Input = history
                                    },
                                    cancellationToken).ConfigureAwait(false);
                                if (openAiWebSocketSession.LastTransportDiagnostics is { } failedWebSocket)
                                {
                                    response = response with { Transport = failedWebSocket with
                                    {
                                        Attempts = failedWebSocket.Attempts.Concat(response.Transport?.Attempts ?? []).ToArray()
                                    } };
                                }
                            }
                        }
                        else
                        {
                            response = await openAiClient.CreateResponseAsync(
                                openAiRequest,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        modelPassStopwatch.Stop();
                        var failedTransport = attemptedWebSocket ? openAiWebSocketSession?.LastTransportDiagnostics : null;
                        RecordWarmupUsage(failedTransport);
                        modelPassAudits.Add(new SimulatorAgentModelPassAudit(
                            totalTurns,
                            instructionIndex + 1,
                            turn,
                            modelPassStartedUtc,
                            modelPassStopwatch.ElapsedMilliseconds,
                            false,
                            null,
                            null,
                            null,
                            0,
                            SimulatorAgentTokenUsage.Empty,
                            exception.Message)
                        {
                            Context = modelContextAudit,
                            Transport = failedTransport
                        });
                        ReportProgress(new SimulatorAgentProgress(
                            SimulatorAgentProgressStage.ModelCompleted,
                            $"Model pass {totalTurns} failed after {modelPassStopwatch.ElapsedMilliseconds:N0} ms: {exception.Message}",
                            instructionIndex + 1,
                            instructions.Count,
                            turn));
                        throw;
                    }

                    modelPassStopwatch.Stop();
                    tokenUsage = tokenUsage.Add(response.Tokens);
                    RecordWarmupUsage(response.Transport);
                    modelPassAudits.Add(new SimulatorAgentModelPassAudit(
                        totalTurns,
                        instructionIndex + 1,
                        turn,
                        modelPassStartedUtc,
                        modelPassStopwatch.ElapsedMilliseconds,
                        true,
                        response.ResponseId,
                        response.ResponseModel,
                        request.CaptureTrace
                            ? CreateAuditPayload(response.AssistantText, MaximumAuditAssistantTextCharacters).Content
                            : null,
                        response.FunctionCalls.Count,
                        response.Tokens,
                        null)
                    {
                        Context = modelContextAudit,
                        ResponseServiceTier = response.ResponseServiceTier,
                        Transport = response.Transport
                    });
                    ReportProgress(new SimulatorAgentProgress(
                        SimulatorAgentProgressStage.ModelCompleted,
                        BuildModelPassCompletedMessage(totalTurns, response, modelPassStopwatch.ElapsedMilliseconds),
                        instructionIndex + 1,
                        instructions.Count,
                        turn));
                    conversation.AcceptResponse(response.Output);

                    if (response.FunctionCalls.Count == 0)
                    {
                        conversation.AddUserInput(ReadFeedbackPrompt("continue-with-tool"));
                        continue;
                    }

                    foreach (var receivedCall in response.FunctionCalls)
                    {
                        var call = toolGateway.NormalizeFunctionCall(receivedCall);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.Equals(call.Name, CompleteInstructionToolName, StringComparison.Ordinal))
                        {
                            var completionStartedUtc = DateTimeOffset.UtcNow;
                            var completionStopwatch = Stopwatch.StartNew();
                            var completion = ParseCompletion(call.Arguments);
                            if (appGraphExplorationName is not null
                                && TryBuildAppGraphCompletionRejection(runId, completion, out var rejection))
                            {
                                completionStopwatch.Stop();
                                var rejectionOutput = new JsonObject
                                {
                                    ["accepted"] = false,
                                    ["message"] = rejection
                                }.ToJsonString();
                                conversation.AddFunctionOutput(call.CallId, rejectionOutput);
                                toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                                    toolCallAudits.Count + 1,
                                    instructionIndex + 1,
                                    turn,
                                    call.CallId,
                                    call.Name,
                                    false,
                                    null,
                                    completionStartedUtc,
                                    completionStopwatch.ElapsedMilliseconds,
                                    CreateAuditPayload(
                                        SanitizeToolArguments(call).ToJsonString(),
                                        MaximumAuditArgumentCharacters,
                                        request.CaptureTrace),
                                    CreateAuditPayload(
                                        rejectionOutput,
                                        MaximumAuditResultCharacters,
                                        request.CaptureTrace),
                                    true,
                                    rejection));
                                ReportProgress(new SimulatorAgentProgress(
                                    SimulatorAgentProgressStage.ToolCompleted,
                                    $"{CompleteInstructionToolName} rejected: {rejection}",
                                    instructionIndex + 1,
                                    instructions.Count,
                                    turn,
                                    CompleteInstructionToolName));
                                conversation.AddUserInput(ReadFeedbackPrompt(
                                    "app-graph-completion-rejected"));
                                turnHadSuccessfulToolCall = true;
                                continue;
                            }
                            if (appGraphExplorationName is not null)
                            {
                                appGraphLiveRuns.MergeCompletionSummary(runId, completion.Summary, turn);
                            }
                            completionStopwatch.Stop();
                            conversation.AddFunctionOutput(call.CallId, completion.Output);
                            toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                                toolCallAudits.Count + 1,
                                instructionIndex + 1,
                                turn,
                                call.CallId,
                                call.Name,
                                false,
                                null,
                                completionStartedUtc,
                                completionStopwatch.ElapsedMilliseconds,
                                CreateAuditPayload(
                                    SanitizeToolArguments(call).ToJsonString(),
                                    MaximumAuditArgumentCharacters,
                                    request.CaptureTrace),
                                CreateAuditPayload(
                                    completion.Output,
                                    MaximumAuditResultCharacters,
                                    request.CaptureTrace),
                                false,
                                completion.Summary));
                            var status = completion.Succeeded
                                ? SimulatorAgentInstructionStatus.Succeeded
                                : SimulatorAgentInstructionStatus.Failed;
                            var result = new SimulatorAgentInstructionResult(
                                instructionIndex + 1,
                                instruction,
                                status,
                                completion.Summary,
                                turn,
                                instructionToolCalls);
                            results.Add(result);
                            ReportProgress(new SimulatorAgentProgress(
                                SimulatorAgentProgressStage.InstructionCompleted,
                                completion.Summary,
                                instructionIndex + 1,
                                instructions.Count,
                                turn,
                                CompleteInstructionToolName));
                            instructionCompleted = true;
                            if (!completion.Succeeded && !request.ContinueAfterInstructionFailure)
                            {
                                return Finish(
                                    SimulatorAgentRunStatus.Failed,
                                    $"Instruction {instructionIndex + 1} failed: {completion.Summary}");
                            }

                            break;
                        }

                        if (string.Equals(call.Name, ReportAppGraphProgressToolName, StringComparison.Ordinal))
                        {
                            var reportStartedUtc = DateTimeOffset.UtcNow;
                            var reportStopwatch = Stopwatch.StartNew();
                            var update = appGraphLiveRuns.ApplyReport(runId, call.Arguments, turn);
                            if (update.IsSuccess)
                            {
                                appGraphCurrentDestinationId = ReadString(
                                    call.Arguments,
                                    "currentDestinationId");
                            }
                            reportStopwatch.Stop();
                            var reportOutput = new JsonObject
                            {
                                ["accepted"] = update.IsSuccess,
                                ["message"] = update.Message,
                                ["destinationsAccepted"] = update.DestinationsAccepted,
                                ["transitionsAccepted"] = update.TransitionsAccepted,
                                ["actionsAccepted"] = update.ActionsAccepted,
                                ["navigationHostsAccepted"] = update.NavigationHostsAccepted,
                                ["tabGroupsAccepted"] = update.TabGroupsAccepted,
                                ["totalDestinations"] = update.TotalDestinations,
                                ["totalTransitions"] = update.TotalTransitions,
                                ["pendingActionCount"] = update.PendingActions.Count,
                                ["nextActions"] = new JsonArray(update.PendingActions
                                    .Take(8)
                                    .Select(static action => (JsonNode?)new JsonObject
                                    {
                                        ["id"] = action.Id,
                                        ["destinationId"] = action.DestinationId,
                                        ["toolName"] = action.ToolName,
                                        ["automationId"] = action.AutomationId,
                                        ["semanticMeaning"] = action.SemanticMeaning,
                                        ["status"] = action.Status,
                                        ["attemptCount"] = action.AttemptCount,
                                        ["lastOutcome"] = action.LastOutcome
                                    })
                                    .ToArray())
                            }.ToJsonString();
                            conversation.AddFunctionOutput(call.CallId, reportOutput);
                            toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                                toolCallAudits.Count + 1,
                                instructionIndex + 1,
                                turn,
                                call.CallId,
                                call.Name,
                                false,
                                null,
                                reportStartedUtc,
                                reportStopwatch.ElapsedMilliseconds,
                                CreateAuditPayload(
                                    SanitizeToolArguments(call).ToJsonString(),
                                    MaximumAuditArgumentCharacters,
                                    request.CaptureTrace),
                                CreateAuditPayload(
                                    reportOutput,
                                    MaximumAuditResultCharacters,
                                    request.CaptureTrace),
                                !update.IsSuccess,
                                update.Message));
                            ReportProgress(new SimulatorAgentProgress(
                                SimulatorAgentProgressStage.AppGraphUpdated,
                                update.Message,
                                instructionIndex + 1,
                                instructions.Count,
                                turn,
                                ReportAppGraphProgressToolName));
                            continue;
                        }

                        if (totalToolCalls >= request.MaximumToolCalls)
                        {
                            var summary = $"Stopped after reaching the {request.MaximumToolCalls}-tool-call run limit.";
                            results.Add(new SimulatorAgentInstructionResult(
                                instructionIndex + 1,
                                instruction,
                                SimulatorAgentInstructionStatus.Failed,
                                summary,
                                turn,
                                instructionToolCalls));
                            return Finish(SimulatorAgentRunStatus.Failed, summary);
                        }

                        totalToolCalls++;
                        instructionToolCalls++;
                        activeInstructionToolCalls = instructionToolCalls;
                        var appGraphScrollScope = appGraphExplorationName is not null
                                                  && string.Equals(
                                                      call.Name,
                                                      "ansight_scroll_ui",
                                                      StringComparison.Ordinal)
                            ? BuildAppGraphScrollScope(call.Arguments)
                            : null;
                        var appGraphStableActionScope = appGraphExplorationName is not null
                            ? BuildAppGraphStableActionScope(
                                appGraphCurrentDestinationId,
                                call)
                            : null;
                        ReportProgress(new SimulatorAgentProgress(
                            SimulatorAgentProgressStage.CallingTool,
                            BuildToolCallMessage(call),
                            instructionIndex + 1,
                            instructions.Count,
                            turn,
                            call.Name));
                        if (appGraphExplorationName is not null)
                        {
                            appGraphLiveRuns.UpdateActivity(
                                runId,
                                BuildToolCallMessage(call),
                                turn,
                                call.Name);
                        }
                        var correlationId = RunRequestContext.CreateCorrelationId(
                            allowScreenshotOcr: sessionCapabilities.IsDeviceOnly || appGraphExplorationName is null);
                        var toolCallSignature = BuildToolCallSignature(call);
                        var toolStartedUtc = DateTimeOffset.UtcNow;
                        var toolStopwatch = Stopwatch.StartNew();
                        ToolCallResult toolResult;
                        ToolLoadResult? loadedTools = null;
                        var toolWasDispatched = false;
                        try
                        {
                            var isReadOnlyTool = IsReadOnlyAgentTool(call, sessionId);
                            var eligibleRepositoryTasks = repositoryTasks
                                .Where(task => !excludedRepositoryTaskIds.Contains(task.TaskId))
                                .ToArray();
                            if (toolCatalog is not null && call.Name == AgentToolCatalog.LoadToolName)
                            {
                                loadedTools = toolCatalog.Load(call.Arguments);
                                toolResult = loadedTools.Result;
                            }
                            else if (toolCatalog?.IsDeferred(call.Name) == true)
                            {
                                toolResult = CreateAgentGuardResult("toolLoadingRequired",
                                    $"Load the capability containing {call.Name} with {AgentToolCatalog.LoadToolName} before calling it.");
                            }
                            else if (string.Equals(
                                    call.Name,
                                    DeclareUncoveredStepToolName,
                                    StringComparison.Ordinal))
                            {
                                toolResult = CreateUncoveredStepDeclarationResult(
                                    call.Arguments,
                                    eligibleRepositoryTasks,
                                    attemptedRepositoryTaskIds,
                                    successfulRepositoryTaskIds,
                                    failedRepositoryTaskIds);
                                if (!toolResult.IsError)
                                {
                                    taskReassessmentRequired = JsonNode.Parse(toolResult.Output)?["manualUiAllowed"]?.GetValue<bool>() != true;
                                    if (ReadString(call.Arguments, "reason") is "scope-mismatch" or "missing-input"
                                        && ReadString(call.Arguments, "relatedTaskId") is { } excludedTaskId)
                                    {
                                        excludedRepositoryTaskIds.Add(excludedTaskId);
                                    }
                                }
                            }
                            else if (taskReassessmentRequired
                                     && IsManualUiMutationTool(call.Name)
                                     && TryBuildTaskReassessmentGuardMessage(
                                         call.Name,
                                         eligibleRepositoryTasks,
                                         attemptedRepositoryTaskIds,
                                         failedRepositoryTaskIds,
                                         out var taskReassessmentMessage))
                            {
                                toolResult = CreateAgentGuardResult(
                                    "taskReassessmentGuard",
                                    taskReassessmentMessage);
                            }
                            else if (appGraphExplorationName is not null
                                     && !sessionCapabilities.IsDeviceOnly
                                     && string.Equals(call.Name, "ansight_scan_screen", StringComparison.Ordinal))
                            {
                                toolResult = CreateAgentGuardResult(
                                    "screenScanGuard",
                                    "Screenshot OCR and screen scanning are disabled for agent execution. Use accessibility or visual-tree semantics and report unrepresented content as an observability gap.");
                            }
                            else if (string.Equals(call.Name, "ansight_find_ui", StringComparison.Ordinal)
                                && !HasFocusedUiSelector(call.Arguments))
                            {
                                toolResult = CreateAgentGuardResult(
                                    "focusedQueryGuard",
                                    "Agent execution does not allow broad find_ui enumeration. Reuse the current live visual-tree observation or provide a focused selector.");
                            }
                            else if (appGraphScrollScope is not null
                                     && TryBuildAppGraphScrollGuardMessage(
                                         appGraphScrollScope,
                                         appGraphScrollAttemptsByScope,
                                         appGraphTerminalScrollScopes,
                                         out var scrollGuardMessage))
                            {
                                toolResult = CreateAgentGuardResult(
                                    "appGraphScrollGuard",
                                    scrollGuardMessage);
                            }
                            else if (appGraphStableActionScope is not null
                                     && appGraphLiveRuns.TryBuildActionGuardMessage(
                                         runId,
                                         appGraphStableActionScope.DestinationId,
                                         appGraphStableActionScope.ToolName,
                                         appGraphStableActionScope.AutomationId,
                                         MaximumAppGraphStableActionAttemptsPerScope,
                                         out var stableActionGuardMessage))
                            {
                                toolResult = CreateAgentGuardResult(
                                    "appGraphStableActionGuard",
                                    stableActionGuardMessage);
                            }
                            else if (isReadOnlyTool && readOnlyStagnationWarningIssued)
                            {
                                stagnationSummary = $"Instruction stopped after {consecutiveReadOnlyToolCalls} consecutive read-only tool calls without an action or completion.";
                                toolResult = new ToolCallResult(
                                    true,
                                    new JsonObject
                                    {
                                        ["isError"] = true,
                                        ["stagnationGuard"] = true,
                                        ["message"] = stagnationSummary
                                    }.ToJsonString(),
                                    stagnationSummary);
                            }
                            else if (repeatedToolFailures.TryGetValue(toolCallSignature, out var previousFailureCount)
                                && previousFailureCount >= 2)
                            {
                                toolResult = new ToolCallResult(
                                    true,
                                    new JsonObject
                                    {
                                        ["isError"] = true,
                                        ["circuitBreaker"] = true,
                                        ["message"] = "This exact tool call already failed twice. Change selector, arguments, or evidence source."
                                    }.ToJsonString(),
                                    "This exact tool call already failed twice; the local circuit breaker skipped another execution.");
                            }
                            else if (cameraChangeVerified
                                && string.Equals(call.Name, "ansight_swipe_ui", StringComparison.Ordinal))
                            {
                                toolResult = new ToolCallResult(
                                    false,
                                    new JsonObject
                                    {
                                        ["isError"] = false,
                                        ["performed"] = false,
                                        ["skipped"] = true,
                                        ["cameraChangeAlreadyVerified"] = true,
                                        ["message"] = "A camera-state difference has already verified this instruction. Complete it now."
                                    }.ToJsonString(),
                                    "Swipe skipped because a camera-state difference is already verified; complete the instruction now.");
                            }
                            else
                            {
                                toolWasDispatched = true;
                                toolResult = await toolGateway.ExecuteAsync(
                                    call.Name,
                                    call.Arguments,
                                    sessionId,
                                    correlationId,
                                    cancellationToken,
                                    request.OperationContext);
                                toolResult = RejectSearchInputEcho(call, toolResult, lastTypedText, instruction);
                                if (!toolResult.IsError
                                    && !sessionCapabilities.IsDeviceOnly
                                    && string.Equals(call.Name, "ansight_launch_app", StringComparison.Ordinal)
                                    && !string.IsNullOrWhiteSpace(appId))
                                {
                                    var connectedSession = await toolGateway.WaitForConnectedSessionAsync(
                                        sessionId,
                                        appId,
                                        request.TargetDeviceIdentifier,
                                        TimeSpan.FromSeconds(12),
                                        cancellationToken);
                                    if (connectedSession is not null
                                        && !string.Equals(
                                            connectedSession.SessionId,
                                            sessionId,
                                            StringComparison.Ordinal))
                                    {
                                        toolGateway.RebindRun(
                                            connectedSession.SessionId,
                                            request.TargetDeviceIdentifier);
                                        var previousSessionId = sessionId;
                                        sessionId = connectedSession.SessionId;
                                        sessionContext = connectedSession;
                                        if (appGraphExplorationName is not null)
                                        {
                                            appGraphLiveRuns.RebindSession(runId, sessionId);
                                        }
                                        toolResult = AddSessionRebindResult(
                                            toolResult,
                                            previousSessionId,
                                            sessionId);
                                    }
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            toolStopwatch.Stop();
                            toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                                toolCallAudits.Count + 1,
                                instructionIndex + 1,
                                turn,
                                call.CallId,
                                call.Name,
                                true,
                                correlationId,
                                toolStartedUtc,
                                toolStopwatch.ElapsedMilliseconds,
                                CreateAuditPayload(
                                    SanitizeToolArguments(call).ToJsonString(),
                                    MaximumAuditArgumentCharacters,
                                    request.CaptureTrace),
                                CreateAuditPayload(
                                    string.Empty,
                                    MaximumAuditResultCharacters,
                                    request.CaptureTrace),
                                true,
                                exception.Message));
                            ReportProgress(new SimulatorAgentProgress(
                                SimulatorAgentProgressStage.ToolCompleted,
                                $"{call.Name} threw after {toolStopwatch.ElapsedMilliseconds:N0} ms: {exception.Message}",
                                instructionIndex + 1,
                                instructions.Count,
                                turn,
                                call.Name));
                            throw;
                        }

                        toolStopwatch.Stop();
                        toolCallAudits.Add(new SimulatorAgentToolCallAudit(
                            toolCallAudits.Count + 1,
                            instructionIndex + 1,
                            turn,
                            call.CallId,
                            call.Name,
                            true,
                            correlationId,
                            toolStartedUtc,
                            toolStopwatch.ElapsedMilliseconds,
                            CreateAuditPayload(
                                SanitizeToolArguments(call).ToJsonString(),
                                MaximumAuditArgumentCharacters,
                                request.CaptureTrace),
                            CreateAuditPayload(
                                toolResult.Output,
                                MaximumAuditResultCharacters,
                                request.CaptureTrace),
                            toolResult.IsError,
                            toolResult.Message)
                        {
                            TaskCalls = request.CaptureTrace ? toolResult.TaskCalls : null,
                            TaskSource = request.CaptureTrace ? toolResult.TaskSource : null,
                            OcrEvidence = CreateOcrTraceEvidence(
                                toolResult.TraceEvidence,
                                request.CaptureTrace),
                            AccessibilityEvidence = CreateAccessibilityTraceEvidence(
                                toolResult.AccessibilityEvidence,
                                request.CaptureTrace)
                        });
                        conversation.AddFunctionOutput(call.CallId, toolResult.ModelOutput ?? toolResult.Output);
                        if (loadedTools is { AdditionalTools.Count: > 0 })
                        {
                            conversation.AddToolCapabilities(loadedTools.AdditionalTools, loadedTools.Guidance);
                        }
                        if (IsManualUiMutationTool(call.Name)
                            || call.Name is "ansight_get_live_visual_tree" or "ansight_get_live_navigation_structure"
                                or "ansight_find_ui" or "ansight_wait_for_ui" or "ansight_assert_ui")
                        {
                            conversation.AddGuidance(navigationGuidance.TakeNewGuidance(
                                toolResult.ModelOutput ?? toolResult.Output));
                        }
                        lastToolFailure = toolResult.IsError
                            ? $"{call.Name}: {toolResult.Message}"
                            : null;
                        turnHadSuccessfulToolCall |= !toolResult.IsError;
                        if (appGraphExplorationName is not null
                            && appGraphStableActionScope is not null)
                        {
                            appGraphLiveRuns.RecordActionAttempt(
                                runId,
                                appGraphStableActionScope.DestinationId,
                                appGraphStableActionScope.ToolName,
                                appGraphStableActionScope.Selector,
                                !toolResult.IsError,
                                toolResult.Message);
                        }
                        if (appGraphExplorationName is not null && !toolResult.IsError)
                        {
                            if (appGraphScrollScope is not null)
                            {
                                appGraphScrollAttemptsByScope[appGraphScrollScope] =
                                    appGraphScrollAttemptsByScope.TryGetValue(
                                        appGraphScrollScope,
                                        out var previousScrollAttempts)
                                        ? previousScrollAttempts + 1
                                        : 1;
                                if (HasUnchangedSemanticViewport(toolResult.Output))
                                {
                                    appGraphTerminalScrollScopes.Add(appGraphScrollScope);
                                    conversation.AddUserInput(ReadFeedbackPrompt(
                                        "app-graph-scroll-terminal"));
                                }
                            }
                            else if (IsAppGraphNavigationAction(call.Name))
                            {
                                appGraphCurrentDestinationId = null;
                                appGraphScrollAttemptsByScope.Clear();
                                appGraphTerminalScrollScopes.Clear();
                            }
                        }
                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_list_tasks", StringComparison.Ordinal))
                        {
                            conversation.AddUserInput(ReadFeedbackPrompt(
                                "task-shortcut-assessment"));
                        }

                        if (string.Equals(call.Name, "ansight_run_task", StringComparison.Ordinal))
                        {
                            var taskId = ReadString(call.Arguments, "taskId");
                            if (taskId is not null
                                && repositoryTasks.Any(task => string.Equals(
                                    task.TaskId,
                                    taskId,
                                    StringComparison.Ordinal)))
                            {
                                attemptedRepositoryTaskIds.Add(taskId);
                                if (toolResult.IsError)
                                {
                                    failedRepositoryTaskIds.Add(taskId);
                                    successfulRepositoryTaskIds.Remove(taskId);
                                }
                                else
                                {
                                    successfulRepositoryTaskIds.Add(taskId);
                                    failedRepositoryTaskIds.Remove(taskId);
                                }
                            }
                            taskReassessmentRequired = failedRepositoryTaskIds.Count > 0
                                                       || repositoryTasks.Any(
                                                           task => !attemptedRepositoryTaskIds.Contains(task.TaskId)
                                                                   && !excludedRepositoryTaskIds.Contains(task.TaskId));
                            if (toolResult.IsError)
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt("task-failed"));
                            }
                        }

                        if (toolWasDispatched && RequiresTaskReassessmentAfterAction(call, toolResult))
                        {
                            taskReassessmentRequired = failedRepositoryTaskIds.Count > 0
                                                       || repositoryTasks.Any(
                                                           task => !attemptedRepositoryTaskIds.Contains(task.TaskId)
                                                                   && !excludedRepositoryTaskIds.Contains(task.TaskId));
                        }

                        var repositoryTaskState = BuildRepositoryTaskState(
                            repositoryTasks, attemptedRepositoryTaskIds, failedRepositoryTaskIds,
                            excludedRepositoryTaskIds, taskReassessmentRequired);
                        if (!string.Equals(repositoryTaskState, lastRepositoryTaskState, StringComparison.Ordinal))
                        {
                            conversation.AddUserInput(repositoryTaskState);
                            lastRepositoryTaskState = repositoryTaskState;
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_call_app_tool", StringComparison.Ordinal))
                        {
                            RememberSuccessfulAppToolCall(call, knownSuccessfulAppToolCalls);
                            conversation.AddUserInput(ReadFeedbackPrompt("app-tool-result"));

                            if (TryReadCameraStateSignature(toolResult.Output, out var currentCameraStateSignature))
                            {
                                if (awaitingPostSwipeCameraState && lastCameraStateSignature is not null)
                                {
                                    cameraChangeVerified = !string.Equals(
                                        lastCameraStateSignature,
                                        currentCameraStateSignature,
                                        StringComparison.Ordinal);
                                    conversation.AddUserInput(
                                        cameraChangeVerified
                                            ? ReadFeedbackPrompt("camera-changed")
                                            : ReadFeedbackPrompt("camera-unchanged"));
                                    awaitingPostSwipeCameraState = false;
                                }

                                lastCameraStateSignature = currentCameraStateSignature;
                            }

                            if (TryReadNamedTargetTapArguments(
                                    toolResult.Output,
                                    instruction,
                                    out var currentTargetTapArguments))
                            {
                                conversation.AddUserInput(RenderFeedbackPrompt(
                                        "retained-target-tap",
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["TAP_ARGUMENTS"] = currentTargetTapArguments.ToJsonString()
                                        }));
                            }
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_tap_ui", StringComparison.Ordinal)
                            && call.Arguments["targetX"] is not null
                            && call.Arguments["targetY"] is not null)
                        {
                            conversation.AddUserInput(ReadFeedbackPrompt(
                                "target-tap-delivered"));
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_type_text", StringComparison.Ordinal)
                            && call.Arguments["value"] is JsonValue typedTextValue
                            && typedTextValue.TryGetValue<string>(out var typedText)
                            && !string.IsNullOrWhiteSpace(typedText))
                        {
                            lastTypedText = typedText;
                            conversation.AddUserInput(ReadFeedbackPrompt("text-entry-delivered"));
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_find_ui", StringComparison.Ordinal)
                            && ContainsActionDirective(instruction))
                        {
                            if (IsTypedSearchResultQuery(call.Arguments, lastTypedText)
                                && HasZeroUiMatches(toolResult.Output))
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt(
                                    "query-not-represented"));
                            }
                            else if (IsTypedSearchResultQuery(call.Arguments, lastTypedText)
                                     && HasUiMatches(toolResult.Output)
                                     && !InstructionRequestsActingOnTypedResult(instruction, lastTypedText))
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt(
                                    "search-result-verified"));
                            }
                            else if (TryReadSingleUiTapHint(toolResult.Output))
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt("tap-hint"));
                            }
                            else if (HasZeroUiMatches(toolResult.Output)
                                     && HasTextSelectorWithEnabledRequired(call.Arguments))
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt(
                                    "enabled-filter-no-match"));
                            }
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_scroll_ui", StringComparison.Ordinal)
                            && ContainsActionDirective(instruction))
                        {
                            conversation.AddUserInput(ReadFeedbackPrompt("scroll-delivered"));
                        }

                        if (!toolResult.IsError
                            && string.Equals(call.Name, "ansight_swipe_ui", StringComparison.Ordinal)
                            && lastCameraStateSignature is not null
                            && !cameraChangeVerified)
                        {
                            awaitingPostSwipeCameraState = true;
                            conversation.AddUserInput(ReadFeedbackPrompt("swipe-delivered"));
                        }

                        if (IsReadOnlyAgentTool(call, sessionId))
                        {
                            consecutiveReadOnlyToolCalls++;
                            if (consecutiveReadOnlyToolCalls >= MaximumConsecutiveReadOnlyToolCalls)
                            {
                                readOnlyStagnationWarningIssued = true;
                                conversation.AddUserInput(RenderFeedbackPrompt(
                                        "stagnation-warning",
                                        new Dictionary<string, string>(StringComparer.Ordinal)
                                        {
                                            ["READ_ONLY_TOOL_CALLS"] = consecutiveReadOnlyToolCalls.ToString()
                                        }));
                            }
                        }
                        else if (!toolResult.IsError
                                 && call.Name != DeclareUncoveredStepToolName)
                        {
                            consecutiveReadOnlyToolCalls = 0;
                            readOnlyStagnationWarningIssued = false;
                        }

                        if (toolResult.IsError)
                        {
                            var updatedFailureCount = repeatedToolFailures.TryGetValue(toolCallSignature, out var currentFailureCount)
                                ? currentFailureCount + 1
                                : 1;
                            repeatedToolFailures[toolCallSignature] = updatedFailureCount;
                            if (updatedFailureCount == 2)
                            {
                                conversation.AddUserInput(ReadFeedbackPrompt(
                                    "repeated-tool-failure"));
                            }
                        }
                        else
                        {
                            repeatedToolFailures.Remove(toolCallSignature);
                        }
                        ReportProgress(new SimulatorAgentProgress(
                            SimulatorAgentProgressStage.ToolCompleted,
                            toolResult.IsError
                                ? $"{call.Name} failed after {toolStopwatch.ElapsedMilliseconds:N0} ms: {toolResult.Message}"
                                : $"{call.Name} completed in {toolStopwatch.ElapsedMilliseconds:N0} ms: {toolResult.Message}",
                            instructionIndex + 1,
                            instructions.Count,
                            turn,
                            call.Name));

                        if (stagnationSummary is not null)
                        {
                            results.Add(new SimulatorAgentInstructionResult(
                                instructionIndex + 1,
                                instruction,
                                SimulatorAgentInstructionStatus.Failed,
                                stagnationSummary,
                                turn,
                                instructionToolCalls));
                            ReportProgress(new SimulatorAgentProgress(
                                SimulatorAgentProgressStage.InstructionCompleted,
                                stagnationSummary,
                                instructionIndex + 1,
                                instructions.Count,
                                turn));
                            instructionCompleted = true;
                            if (!request.ContinueAfterInstructionFailure)
                            {
                                return Finish(SimulatorAgentRunStatus.Failed, stagnationSummary);
                            }
                            break;
                        }
                    }

                    if (instructionCompleted)
                    {
                        break;
                    }

                    if (!isCompletionGracePass
                        && turn == request.MaximumTurnsPerInstruction
                        && totalTurns < request.MaximumRoundTrips
                        && turnHadSuccessfulToolCall
                        && lastToolFailure is null)
                    {
                        allowCompletionGracePass = true;
                    }
                }

                if (!instructionCompleted)
                {
                    var summary = roundTripBudgetExhausted
                        ? $"Run exhausted its {request.MaximumRoundTrips}-round-trip organisation limit."
                        : completionGracePassUsed
                        ? $"Instruction exhausted its {request.MaximumTurnsPerInstruction}-turn work budget and did not call {CompleteInstructionToolName} during the completion-only pass."
                        : lastToolFailure is null
                        ? $"Instruction exceeded its {request.MaximumTurnsPerInstruction}-turn limit without calling {CompleteInstructionToolName}."
                        : $"Instruction exceeded its {request.MaximumTurnsPerInstruction}-turn limit. Last tool error: {lastToolFailure}";
                    results.Add(new SimulatorAgentInstructionResult(
                        instructionIndex + 1,
                        instruction,
                        SimulatorAgentInstructionStatus.Failed,
                        summary,
                        activeInstructionTurn,
                        instructionToolCalls));
                    if (roundTripBudgetExhausted)
                    {
                        return Finish(SimulatorAgentRunStatus.Failed, summary);
                    }
                    if (!request.ContinueAfterInstructionFailure)
                    {
                        return Finish(SimulatorAgentRunStatus.Failed, summary);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledIndex = Math.Min(results.Count + 1, instructions.Count);
            if (results.Count < instructions.Count)
            {
                results.Add(new SimulatorAgentInstructionResult(
                    cancelledIndex,
                    instructions[cancelledIndex - 1],
                    SimulatorAgentInstructionStatus.Cancelled,
                    "Run cancelled locally.",
                    activeInstructionTurn,
                    activeInstructionToolCalls));
            }

            return Finish(SimulatorAgentRunStatus.Cancelled, "Simulator agent run cancelled.");
        }
        catch (Exception exception)
        {
            if (results.Count < instructions.Count)
            {
                var failedIndex = Math.Min(Math.Max(activeInstructionIndex, 1), instructions.Count);
                results.Add(new SimulatorAgentInstructionResult(
                    failedIndex,
                    instructions[failedIndex - 1],
                    SimulatorAgentInstructionStatus.Failed,
                    exception.Message,
                    activeInstructionTurn,
                    activeInstructionToolCalls));
            }

            return Finish(SimulatorAgentRunStatus.Failed, exception.Message);
        }

        var failedInstructionCount = results.Count(static result =>
            result.Status == SimulatorAgentInstructionStatus.Failed);
        var firstFailedInstruction = results.FirstOrDefault(static result =>
            result.Status == SimulatorAgentInstructionStatus.Failed);
        var message = failedInstructionCount == 0
            ? $"Completed {instructions.Count} instruction(s) in {totalTurns} model turn(s)."
            : $"Completed all {instructions.Count} instruction(s) in {totalTurns} model turn(s); "
              + $"{failedInstructionCount} instruction(s) failed. First failure: instruction "
              + $"{firstFailedInstruction!.Index}: {firstFailedInstruction.Summary}";
        return Finish(
            failedInstructionCount == 0
                ? SimulatorAgentRunStatus.Succeeded
                : SimulatorAgentRunStatus.Failed,
            message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        toolGateway.EndRun();
        openAiClient.Dispose();
    }

    private static IReadOnlyList<string> NormalizeInstructions(
        IReadOnlyList<string>? instructions,
        int maximumInstructionCharacters)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (maximumInstructionCharacters is < 1 or > MaximumInstructionCharactersLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInstructionCharacters),
                $"Maximum instruction characters must be between 1 and {MaximumInstructionCharactersLimit}.");
        }
        var normalized = instructions
            .Select(static instruction => instruction?.Trim())
            .Where(static instruction => !string.IsNullOrWhiteSpace(instruction))
            .Select(static instruction => instruction!)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("At least one simulator instruction is required.", nameof(instructions));
        }

        if (normalized.Length > MaximumInstructionCount)
        {
            throw new ArgumentException($"A run can contain at most {MaximumInstructionCount} instructions.", nameof(instructions));
        }

        if (normalized.Any(instruction => instruction.Length > maximumInstructionCharacters))
        {
            throw new ArgumentException(
                $"Each simulator instruction must be {maximumInstructionCharacters} characters or fewer.",
                nameof(instructions));
        }

        return normalized;
    }

    private static void ValidateBudgets(
        int maximumTurnsPerInstruction,
        int maximumRoundTrips,
        int maximumToolCalls,
        int maximumModelOutputTokens)
    {
        if (maximumTurnsPerInstruction is < 1 or > MaximumTurnsPerInstructionLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTurnsPerInstruction),
                $"Maximum turns per instruction must be between 1 and {MaximumTurnsPerInstructionLimit}.");
        }

        if (maximumRoundTrips is < 1 or > MaximumRoundTripsLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRoundTrips),
                $"Maximum round trips must be between 1 and {MaximumRoundTripsLimit}.");
        }

        if (maximumToolCalls is < 1 or > MaximumToolCallsLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumToolCalls),
                $"Maximum tool calls must be between 1 and {MaximumToolCallsLimit}.");
        }

        if (maximumModelOutputTokens is < 1 or > MaximumModelOutputTokensLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumModelOutputTokens),
                $"Maximum model output tokens must be between 1 and {MaximumModelOutputTokensLimit}.");
        }
    }

    private static string ReadFeedbackPrompt(string name)
        => ReadPromptSection("feedback.md", name);

    private static string BuildRepositoryTaskState(
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks,
        IReadOnlySet<string> attemptedTaskIds,
        IReadOnlySet<string> failedTaskIds,
        IReadOnlySet<string> excludedTaskIds,
        bool reassessmentRequired)
    {
        if (repositoryTasks.Count == 0)
        {
            return string.Empty;
        }

        var remainingTaskIds = repositoryTasks.Select(task => task.TaskId)
            .Where(taskId => !attemptedTaskIds.Contains(taskId) && !excludedTaskIds.Contains(taskId));
        return "Repository task state: " + new JsonObject
        {
            ["remainingTaskIds"] = TaskIds(remainingTaskIds),
            ["failedTaskIds"] = TaskIds(failedTaskIds),
            ["excludedTaskIds"] = TaskIds(excludedTaskIds),
            ["reassessmentRequired"] = reassessmentRequired
        }.ToJsonString();

        static JsonArray TaskIds(IEnumerable<string> taskIds)
            => new(taskIds.Order(StringComparer.Ordinal).Select(taskId => (JsonNode?)JsonValue.Create(taskId)).ToArray());
    }

    internal static string BuildAgentInstructions(
        bool hasUsableAppGraphRoute,
        bool isAppGraphExploration,
        SessionCapabilities capabilities,
        bool includeToolGuidance = true)
    {
        if (capabilities.IsDeviceOnly)
            return EmbeddedTextResource.Read("SimulatorAgent/Prompts/device-execution.md");
        var core = EmbeddedTextResource.Render(
                "SimulatorAgent/Prompts/simulator-agent-instructions.md",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["VISUAL_TREE_PROVIDER_INSTRUCTIONS"] = BuildVisualTreeProviderInstructions(capabilities),
                    ["SCREEN_SCAN_INSTRUCTIONS"] = isAppGraphExploration
                        ? "Screenshot tools save artifacts without sending pixels; OCR and screen scanning are unavailable for App Graph exploration."
                        : "If a named visible target is missing from semantic trees, use ansight_scan_screen or a focused ansight_find_ui text query to check the current screenshot. OCR matches are current-layout evidence: use a unique exact match, verify the result after tapping, and never treat OCR as a stable automation ID.",
                    ["APP_GRAPH_FALLBACK_INSTRUCTIONS"] = hasUsableAppGraphRoute || isAppGraphExploration
                        ? "Use authorized App Graph routes when they cover the requested transition."
                        : string.Empty,
                    ["APP_GRAPH_ROUTE_INSTRUCTIONS"] = hasUsableAppGraphRoute
                        ? ReadPromptSection("instruction-sections.md", "app-graph-route")
                        : string.Empty,
                    ["APP_GRAPH_TEACHING_INSTRUCTIONS"] = isAppGraphExploration
                        ? ReadPromptSection("instruction-sections.md", "app-graph-teaching")
                        : string.Empty
                })
            .Replace($"{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}",
                $"{Environment.NewLine}{Environment.NewLine}",
                StringComparison.Ordinal)
            .TrimEnd();
        return includeToolGuidance ? $"{core}\n\n{AgentToolCatalog.FullGuidance()}" : core;
    }

    private static string BuildVisualTreeProviderInstructions(
        SessionCapabilities capabilities)
    {
        var instructions = "The default visual tree is a compact device-accessibility digest; virtualized or custom-rendered content may be omitted.";
        if (capabilities.VisualTreeToolIds.Count > 0)
        {
            instructions +=
                $" App profile: {capabilities.ProfileDescription}. For accessibility gaps, request one app tree with toolId: {string.Join(", ", capabilities.VisualTreeToolIds)}.";
        }
        else
        {
            instructions +=
                " No app-tree provider is published; do not invent a toolId.";
        }

        return instructions
               + " Prefer focused queries or subtrees to repeated full-tree reads. Select candidates using observed IDs and bounds.";
    }

    private static string RenderFeedbackPrompt(
        string name,
        IReadOnlyDictionary<string, string> values)
        => RenderPromptSection("feedback.md", name, values);

    private static string ReadPromptSection(string catalogName, string sectionName)
        => EmbeddedTextResource.ReadSection(
            $"SimulatorAgent/Prompts/{catalogName}",
            sectionName);

    private static string RenderPromptSection(
        string catalogName,
        string sectionName,
        IReadOnlyDictionary<string, string> values)
        => EmbeddedTextResource.RenderSection(
            $"SimulatorAgent/Prompts/{catalogName}",
            sectionName,
            values);

    private static void RemoveIrrelevantLifecycleTools(
        JsonArray tools,
        ToolSessionContext? sessionContext,
        string instruction)
    {
        if (sessionContext is not { IsLive: true }
            || !string.Equals(sessionContext.AppState, "foreground", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var needsDeviceLifecycle = MentionsLifecycleIntent(
            instruction,
            "device|simulator|emulator",
            "boot|discover|find|launch|list|restart|start|stop|shutdown");
        var needsAppLifecycle = MentionsLifecycleIntent(
            instruction,
            "app|application",
            "launch|open|recover|relaunch|restart|start|stop|terminate");
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            var name = tools[index]?["name"]?.GetValue<string>();
            if ((!needsDeviceLifecycle
                 && name is "ansight_list_host_devices" or "ansight_start_device")
                || (!needsAppLifecycle
                    && name is "ansight_launch_app" or "ansight_terminate_app"))
            {
                tools.RemoveAt(index);
            }
        }
    }

    private static bool MentionsLifecycleIntent(
        string instruction,
        string targetPattern,
        string actionPattern)
        => Regex.IsMatch(
               instruction,
               $@"(?is)\b(?:{actionPattern})\b.{{0,40}}\b(?:{targetPattern})\b",
               RegexOptions.CultureInvariant)
           || Regex.IsMatch(
               instruction,
               $@"(?is)\b(?:{targetPattern})\b.{{0,40}}\b(?:{actionPattern})\b",
               RegexOptions.CultureInvariant);

    private static JsonArray BuildInitialInput(
        string instruction,
        int instructionIndex,
        int instructionCount,
        IReadOnlyList<SimulatorAgentInstructionResult> previousResults,
        ToolSessionContext? sessionContext,
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks,
        IReadOnlyDictionary<string, JsonObject> knownSuccessfulAppToolCalls,
        IReadOnlyList<SimulatorAgentAppGraphPlan> appGraphPlans,
        SimulatorAgentAppGraphLiveRun? appGraphRun)
    {
        var previousSummary = previousResults.Count == 0
            ? ReadPromptSection("context.md", "previous-results-none")
            : string.Join(
                Environment.NewLine,
                previousResults.Select(result => RenderPromptSection(
                    "context.md",
                    "previous-result",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["INDEX"] = result.Index.ToString(),
                        ["STATUS"] = result.Status.ToString(),
                        ["SUMMARY"] = result.Summary
                    })));
        var prompt = RenderPromptSection(
            "context.md",
            "instruction-input",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INSTRUCTION_NUMBER"] = (instructionIndex + 1).ToString(),
                ["INSTRUCTION_COUNT"] = instructionCount.ToString(),
                ["INSTRUCTION"] = instruction,
                ["SESSION_CONTEXT"] = BuildSessionContextText(sessionContext),
                ["PREVIOUS_RESULTS"] = previousSummary,
                ["REPOSITORY_TASKS_SECTION"] = BuildRepositoryTasksText(repositoryTasks),
                ["KNOWN_APP_TOOL_CALLS"] = BuildKnownAppToolCallsText(knownSuccessfulAppToolCalls),
                ["APP_GRAPH_GUIDANCE_SECTION"] = BuildAppGraphGuidanceText(appGraphPlans),
                ["APP_GRAPH_FRONTIER_SECTION"] = BuildAppGraphFrontierText(appGraphRun)
            });
        return [CreateUserInput(Regex.Replace(prompt, @"(?:\r?\n){3,}", "\n\n").Trim())];
    }

    private static string BuildRepositoryTasksText(
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks)
    {
        if (repositoryTasks.Count == 0)
        {
            return string.Empty;
        }

        var taskSummaries = repositoryTasks.Select((task, index) => RenderPromptSection(
            "context.md",
            "repository-task",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INDEX"] = (index + 1).ToString(),
                ["TOOL_NAME"] = task.ToolName,
                ["TASK_ID"] = task.TaskId
            }));
        return RenderPromptSection(
            "context.md",
            "repository-tasks-section",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TASKS"] = string.Join(Environment.NewLine, taskSummaries)
            });
    }

    private static string BuildAppGraphFrontierText(SimulatorAgentAppGraphLiveRun? run)
    {
        if (run is null)
        {
            return string.Empty;
        }

        var pending = run.Frontier
            .Where(static action => action.Status is "queued" or "attempted")
            .OrderBy(static action => action.AttemptCount)
            .ThenBy(static action => action.FirstObservedUtc)
            .Take(12)
            .Select(action => RenderPromptSection(
                "app-graph.md",
                "frontier-action",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ACTION_ID"] = action.Id,
                    ["DESTINATION_ID"] = action.DestinationId,
                    ["TOOL_NAME"] = action.ToolName,
                    ["AUTOMATION_ID"] = action.AutomationId,
                    ["STATUS"] = action.Status,
                    ["ATTEMPT_COUNT"] = action.AttemptCount.ToString(),
                    ["SEMANTIC_MEANING"] = action.SemanticMeaning
                }))
            .ToArray();
        if (pending.Length == 0)
        {
            return WrapAppGraphFrontierText(RenderPromptSection(
                    "app-graph.md",
                    run.Frontier.Count == 0
                        ? "frontier-empty"
                        : "frontier-terminal",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ACTION_COUNT"] = run.Frontier.Count.ToString(),
                        ["NAVIGATION_HOST_COUNT"] = run.NavigationHosts.Count.ToString(),
                        ["TAB_GROUP_COUNT"] = run.TabGroups.Count.ToString()
                    }));
        }

        return WrapAppGraphFrontierText(RenderPromptSection(
            "app-graph.md",
            "frontier-pending",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DESTINATION_COUNT"] = run.Nodes.Count.ToString(),
                ["TRANSITION_COUNT"] = run.Edges.Count.ToString(),
                ["NAVIGATION_HOST_COUNT"] = run.NavigationHosts.Count.ToString(),
                ["TAB_GROUP_COUNT"] = run.TabGroups.Count.ToString(),
                ["ACTION_COUNT"] = run.Frontier.Count.ToString(),
                ["PENDING_ACTIONS"] = string.Join(Environment.NewLine, pending)
            }));
    }

    private static string WrapAppGraphFrontierText(string frontier)
        => RenderPromptSection(
            "context.md",
            "app-graph-frontier-section",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_GRAPH_FRONTIER"] = frontier
            });

    private static void RemoveUnsupportedAppGraphTools(JsonArray tools)
    {
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            var name = tools[index]?["name"]?.GetValue<string>();
            if (name is "ansight_list_tasks"
                or "ansight_describe_module"
                or "ansight_run_task"
                or "ansight_list_app_tools"
                or "ansight_call_app_tool"
                || name?.StartsWith("ansight_task_", StringComparison.Ordinal) == true)
            {
                tools.RemoveAt(index);
            }
        }
    }

    private static void RemoveScreenScanTool(JsonArray tools)
    {
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    tools[index]?["name"]?.GetValue<string>(),
                    "ansight_scan_screen",
                    StringComparison.Ordinal))
            {
                tools.RemoveAt(index);
            }
        }
    }

    private bool TryBuildAppGraphCompletionRejection(
        string runId,
        SimulatorAgentCompletion completion,
        out string message)
    {
        var run = appGraphLiveRuns.Get(runId);
        if (run is null || run.Nodes.Count == 0)
        {
            message = "No verified destination has been reported to the live App Graph.";
            return true;
        }

        var pendingActions = run.Frontier
            .Where(static action => action.Status is "queued" or "attempted")
            .OrderBy(static action => action.AttemptCount)
            .ThenBy(static action => action.FirstObservedUtc)
            .ToArray();
        var remainingActions = pendingActions.Length > 0
            ? pendingActions.Length
            : Math.Max(
                0,
                run.Coverage.SafeActionsObserved - run.Coverage.ActionsExplored);
        var pendingScrollDestinations = run.Nodes
            .Where(static node => node.ScrollStatus is "unknown" or "in_progress")
            .OrderBy(static node => node.FirstObservedUtc)
            .ToArray();
        var remainingScrollContainers = pendingScrollDestinations.Length > 0
            ? pendingScrollDestinations.Length
            : Math.Max(
                0,
                run.Coverage.ScrollContainersObserved
                - run.Coverage.ScrollContainersCompleted);
        var structureGaps = appGraphLiveRuns.GetStructureGaps(runId);
        if (remainingActions == 0
            && remainingScrollContainers == 0
            && structureGaps.Count == 0)
        {
            if (completion.Succeeded
                && !AppGraphExplorationSummaryValidator.TryValidate(completion.Summary, out var contractError))
            {
                message = $"The App Graph completion summary is invalid: {contractError} Repair the JSON contract and call complete_instruction again.";
                return true;
            }

            message = string.Empty;
            return false;
        }

        var reasons = new List<string>();
        if (remainingActions > 0)
        {
            if (pendingActions.Length > 0)
            {
                var exactActions = pendingActions
                    .Take(8)
                    .Select(static action =>
                        $"{action.Id} at {action.DestinationId} via {action.ToolName}"
                        + (action.AutomationId.Length > 0
                            ? $" automationId={action.AutomationId}"
                            : string.Empty));
                reasons.Add(
                    $"{remainingActions} host-tracked action(s) remain: {string.Join("; ", exactActions)}"
                    + (pendingActions.Length > 8 ? "; additional actions omitted" : string.Empty));
            }
            else
            {
                reasons.Add($"{remainingActions} legacy reported safe action(s) remain unexplored");
            }
        }
        if (remainingScrollContainers > 0)
        {
            var exactDestinations = pendingScrollDestinations
                .Take(8)
                .Select(static node => $"{node.Id} ({node.ScrollStatus})");
            reasons.Add(
                $"{remainingScrollContainers} destination scroll classification(s) remain incomplete: "
                + string.Join("; ", exactDestinations)
                + (pendingScrollDestinations.Length > 8
                    ? "; additional destinations omitted"
                    : string.Empty));
        }
        if (structureGaps.Count > 0)
        {
            reasons.Add(
                $"{structureGaps.Count} navigation-structure gap(s) remain: "
                + string.Join("; ", structureGaps.Take(8))
                + (structureGaps.Count > 8 ? "; additional structure gaps omitted" : string.Empty));
        }

        message = string.Join(" and ", reasons) + ".";
        return true;
    }

    private static string BuildAppGraphGuidanceText(
        IReadOnlyList<SimulatorAgentAppGraphPlan> appGraphPlans)
    {
        var usablePlans = appGraphPlans
            .Where(HasUsableAppGraphPlan)
            .ToArray();
        if (usablePlans.Length == 0)
        {
            return string.Empty;
        }

        var guidance = RenderPromptSection(
            "app-graph.md",
            "guidance",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GRAPH_PLANS"] = string.Join(
                    Environment.NewLine,
                    usablePlans.Select(BuildAppGraphPlanText))
            });
        var boundedGuidance = guidance.Length <= MaximumAppGraphGuidanceCharacters
            ? guidance
            : guidance[..MaximumAppGraphGuidanceCharacters]
              + Environment.NewLine
              + ReadPromptSection("app-graph.md", "guidance-truncated");
        return RenderPromptSection(
            "context.md",
            "app-graph-guidance-section",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_GRAPH_GUIDANCE"] = boundedGuidance
            });
    }

    private static bool HasUsableAppGraphPlan(SimulatorAgentAppGraphPlan plan)
        => plan.Transitions.Any(static transition => transition.Bindings.Count > 0);

    private static string BuildKnownAppToolCallsText(
        IReadOnlyDictionary<string, JsonObject> knownSuccessfulAppToolCalls)
    {
        if (knownSuccessfulAppToolCalls.Count == 0)
        {
            return ReadPromptSection("context.md", "known-app-tool-calls-none");
        }

        return RenderPromptSection(
            "context.md",
            "known-app-tool-calls",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TOOL_CALLS"] = string.Join(
                    Environment.NewLine,
                    knownSuccessfulAppToolCalls.Select(pair => RenderPromptSection(
                        "context.md",
                        "known-app-tool-call",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["TOOL_ID"] = pair.Key,
                            ["ARGUMENTS"] = pair.Value.ToJsonString()
                        })))
            });
    }

    private static string BuildAppGraphPlanText(SimulatorAgentAppGraphPlan plan)
    {
        var transitions = string.Join(
            Environment.NewLine,
            plan.Transitions.Select(BuildAppGraphTransitionText));
        return RenderPromptSection(
            "app-graph.md",
            "plan",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GRAPH_NAME"] = plan.Name,
                ["GRAPH_ID"] = plan.GraphId.ToString("D"),
                ["VERSION_ID"] = plan.VersionId.ToString("D"),
                ["INTENT"] = plan.Intent,
                ["TARGET_STATE"] = plan.TargetState,
                ["TRANSITIONS"] = transitions.Length == 0
                    ? string.Empty
                    : Environment.NewLine + transitions
            });
    }

    private static string BuildAppGraphTransitionText(SimulatorAgentAppGraphTransition transition)
    {
        var edgePostconditions = transition.Postconditions.Count == 0
            ? string.Empty
            : Environment.NewLine + RenderPromptSection(
                "app-graph.md",
                "plan-edge-postconditions",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["POSTCONDITIONS"] = string.Join("; ", transition.Postconditions)
                });
        var bindings = string.Join(
            Environment.NewLine,
            transition.Bindings.Select(BuildAppGraphBindingText));
        return RenderPromptSection(
            "app-graph.md",
            "plan-transition",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INDEX"] = transition.Index.ToString(),
                ["EDGE_ID"] = transition.EdgeId,
                ["FROM_STATE"] = transition.FromState,
                ["TO_STATE"] = transition.ToState,
                ["ACTION"] = transition.Action,
                ["EDGE_POSTCONDITIONS"] = edgePostconditions,
                ["BINDINGS"] = bindings.Length == 0
                    ? string.Empty
                    : Environment.NewLine + bindings
            });
    }

    private static string BuildAppGraphBindingText(SimulatorAgentAppGraphBinding binding)
    {
        var preconditions = binding.Preconditions.Count == 0
            ? string.Empty
            : Environment.NewLine + RenderPromptSection(
                "app-graph.md",
                "plan-binding-preconditions",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PRECONDITIONS"] = string.Join("; ", binding.Preconditions)
                });
        var postconditions = binding.Postconditions.Count == 0
            ? string.Empty
            : Environment.NewLine + RenderPromptSection(
                "app-graph.md",
                "plan-binding-postconditions",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["POSTCONDITIONS"] = string.Join("; ", binding.Postconditions)
                });
        return RenderPromptSection(
            "app-graph.md",
            "plan-binding",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BINDING_ID"] = binding.BindingId.ToString("D"),
                ["PRIORITY"] = binding.Priority.ToString(),
                ["MECHANISM"] = binding.Mechanism,
                ["CONFIDENCE"] = binding.Confidence.ToString("0.###"),
                ["CONFIGURATION"] = binding.Configuration.ToJsonString(),
                ["PRECONDITIONS"] = preconditions,
                ["POSTCONDITIONS"] = postconditions
            });
    }

    private static void RememberSuccessfulAppToolCall(
        OpenAiFunctionCall call,
        IDictionary<string, JsonObject> knownSuccessfulAppToolCalls)
    {
        if (call.Arguments["toolId"] is not JsonValue toolIdValue
            || !toolIdValue.TryGetValue<string>(out var toolId)
            || string.IsNullOrWhiteSpace(toolId)
            || call.Arguments["arguments"] is not JsonObject arguments)
        {
            return;
        }

        knownSuccessfulAppToolCalls[toolId.Trim()] = arguments.DeepClone().AsObject();
    }

    private static bool TryReadCameraStateSignature(string output, out string signature)
    {
        signature = string.Empty;
        try
        {
            if (JsonNode.Parse(output) is not JsonObject envelope
                || envelope["result"]?["payload"]?["result"] is not JsonObject appResult)
            {
                return false;
            }

            var cameraState = appResult["player"]?["guide"]?["cameraState"] as JsonObject;
            if (cameraState is null
                && appResult["players"] is JsonArray players
                && players.FirstOrDefault()?["guide"]?["cameraState"] is JsonObject listedCameraState)
            {
                cameraState = listedCameraState;
            }

            if (cameraState is null)
            {
                return false;
            }

            signature = cameraState.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSingleUiTapHint(string output)
    {
        try
        {
            return JsonNode.Parse(output)?["result"]?["matches"] is JsonArray matches
                   && matches.Count == 1
                   && matches[0]?["tapHint"]?["selector"] is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadNamedTargetTapArguments(
        string output,
        string instruction,
        out JsonObject arguments)
    {
        arguments = new JsonObject();
        try
        {
            if (JsonNode.Parse(output)?["result"]?["payload"]?["result"]?["annotationManagers"]
                    is not JsonArray managers)
            {
                return false;
            }

            var annotation = managers
                .OfType<JsonObject>()
                .SelectMany(static manager =>
                    (manager["annotations"] as JsonArray)?.OfType<JsonObject>()
                    ?? Enumerable.Empty<JsonObject>())
                .Where(candidate =>
                {
                    var label = ReadString(candidate, "label");
                    return label is not null
                           && instruction.Contains(label, StringComparison.OrdinalIgnoreCase)
                           && candidate["tapHint"] is JsonObject;
                })
                .OrderByDescending(static candidate => ReadString(candidate, "label")?.Length ?? 0)
                .FirstOrDefault();
            if (annotation?["tapHint"] is not JsonObject tapHint
                || tapHint["selector"] is not JsonObject selector
                || tapHint["targetX"] is null
                || tapHint["targetY"] is null)
            {
                return false;
            }

            foreach (var property in selector)
            {
                arguments[property.Key] = property.Value?.DeepClone();
            }

            arguments["targetX"] = tapHint["targetX"]!.DeepClone();
            arguments["targetY"] = tapHint["targetY"]!.DeepClone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasZeroUiMatches(string output)
    {
        try
        {
            return JsonNode.Parse(output)?["result"]?["totalMatches"] is JsonValue totalMatches
                   && totalMatches.TryGetValue<int>(out var count)
                   && count == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasUiMatches(string output)
    {
        try
        {
            return JsonNode.Parse(output)?["result"]?["totalMatches"] is JsonValue totalMatches
                   && totalMatches.TryGetValue<int>(out var count)
                   && count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasTextSelectorWithEnabledRequired(JsonObject arguments)
        => arguments["text"] is JsonValue textValue
           && textValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
           && arguments["enabled"] is JsonValue enabledValue
           && enabledValue.TryGetValue<bool>(out var enabled)
           && enabled;

    private static bool IsTypedSearchResultQuery(JsonObject arguments, string? lastTypedText)
        => !string.IsNullOrWhiteSpace(lastTypedText)
           && arguments["text"] is JsonValue textValue
           && textValue.TryGetValue<string>(out var queriedText)
           && string.Equals(queriedText, lastTypedText, StringComparison.OrdinalIgnoreCase);

    private static bool InstructionRequestsActingOnTypedResult(string instruction, string? typedText)
    {
        if (string.IsNullOrWhiteSpace(typedText))
        {
            return false;
        }

        var normalizedInstruction = instruction.ToLowerInvariant();
        var normalizedTarget = typedText.Trim().ToLowerInvariant();
        var directTargets = new[]
        {
            $"select {normalizedTarget}",
            $"choose {normalizedTarget}",
            $"open {normalizedTarget}",
            $"tap {normalizedTarget}",
            $"press {normalizedTarget}"
        };
        return directTargets.Any(normalizedInstruction.Contains)
               || normalizedInstruction.Contains("select the result", StringComparison.Ordinal)
               || normalizedInstruction.Contains("choose the result", StringComparison.Ordinal)
               || normalizedInstruction.Contains("open the result", StringComparison.Ordinal)
               || normalizedInstruction.Contains("open a result", StringComparison.Ordinal)
               || normalizedInstruction.Contains("tap the result", StringComparison.Ordinal);
    }

    private static bool ContainsActionDirective(string instruction)
    {
        var words = instruction.Split(
            [' ', '\t', '\r', '\n', '.', ',', ':', ';', '!', '?', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Any(static word => word.ToLowerInvariant() is
            "select" or "tap" or "press" or "choose" or "open" or "start" or "launch" or "search"
            or "navigate" or "swipe" or "scroll" or "change" or "enter" or "type" or "dismiss" or "close");
    }

    private static string BuildSessionContextText(ToolSessionContext? context)
    {
        if (context is null)
        {
            return ReadPromptSection("context.md", "session-unavailable");
        }

        var launchState = context.IsLive
                          && string.Equals(context.AppState, "foreground", StringComparison.OrdinalIgnoreCase)
            ? ReadPromptSection("context.md", "session-launch-live")
            : ReadPromptSection("context.md", "session-launch-unverified");
        return RenderPromptSection(
            "context.md",
            "session-context",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APP_NAME"] = context.AppName,
                ["APP_ID"] = context.AppId,
                ["SESSION_ID"] = context.SessionId,
                ["DEVICE"] = BuildDeviceContextText(context.Device),
                ["IS_LIVE"] = context.IsLive.ToString(),
                ["APP_STATE"] = context.AppState,
                ["LAUNCH_STATE"] = launchState
            }).TrimEnd();
    }

    private static string BuildDeviceContextText(SimulatorAgentRunDevice? device)
    {
        if (device is null)
        {
            return ReadPromptSection("context.md", "device-unavailable");
        }

        var values = new[]
            {
                device.Manufacturer,
                device.Model,
                string.Join(
                    " ",
                    new[] { device.OperatingSystemName, device.OperatingSystemVersion }
                        .Where(static value => !string.IsNullOrWhiteSpace(value))),
                device.FormFactor,
                device.Identifier
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        return string.Join(" · ", values);
    }

    private static string BuildToolCallMessage(OpenAiFunctionCall call)
    {
        var safeArguments = SanitizeToolArguments(call);
        var argumentText = safeArguments.ToJsonString();
        if (argumentText.Length > 320)
        {
            argumentText = $"{argumentText[..317]}...";
        }

        return $"Calling {call.Name} with {argumentText}.";
    }

    private static ToolCallResult AddSessionRebindResult(
        ToolCallResult result,
        string previousSessionId,
        string sessionId)
    {
        var output = JsonNode.Parse(result.Output) as JsonObject ?? new JsonObject
        {
            ["isError"] = false
        };
        output["sessionRebound"] = new JsonObject
        {
            ["previousSessionId"] = previousSessionId,
            ["sessionId"] = sessionId,
            ["message"] = "The relaunched app connected under a new live session. Future tool calls now target that session."
        };
        return result with
        {
            Output = output.ToJsonString(),
            Message = $"{result.Message} Rebound the run to live session '{sessionId}'."
        };
    }

    private bool IsReadOnlyAgentTool(OpenAiFunctionCall call, string sessionId)
    {
        if (string.Equals(call.Name, "ansight_call_app_tool", StringComparison.Ordinal))
        {
            return toolGateway.IsReadOnlyAppToolCall(call.Arguments, sessionId);
        }

        return call.Name is "ansight_get_live_visual_tree"
            or "ansight_get_live_navigation_structure"
            or "ansight_find_ui"
            or "ansight_scan_screen"
            or "ansight_take_screenshot"
            or "ansight_wait_for_ui"
            or "ansight_assert_ui"
            or "ansight_list_host_devices"
            or "ansight_list_tasks"
            or "ansight_describe_module"
            or "ansight_list_app_tools";
    }

    private static bool IsManualUiMutationTool(string toolName)
        => toolName is "ansight_tap_ui"
            or "ansight_type_text"
            or "ansight_type_secret"
            or "ansight_swipe_ui"
            or "ansight_scroll_ui"
            or "ansight_pinch_ui"
            or "ansight_back_ui"
            or "ansight_run_ui_sequence";

    private static bool RequiresTaskReassessmentAfterAction(
        OpenAiFunctionCall call,
        ToolCallResult result)
    {
        // Text entry and content scrolling can continue within a declared step. Taps, back,
        // and generic swipes may change pages or tabs and must trigger reassessment. A rejected
        // selector did not consume the step either. Unknown/partial failures remain conservative.
        if (!IsManualUiMutationTool(call.Name)
            || call.Name is "ansight_type_text" or "ansight_type_secret" or "ansight_scroll_ui")
        {
            return false;
        }

        try
        {
            var output = JsonNode.Parse(result.Output);
            var content = output?["result"] ?? output;
            return content?["performed"]?.GetValue<bool>() != false;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static ToolCallResult RejectSearchInputEcho(
        OpenAiFunctionCall call,
        ToolCallResult result,
        string? lastTypedText,
        string instruction)
    {
        if (result.IsError || call.Name != "ansight_wait_for_ui"
            || !IsTypedSearchResultQuery(call.Arguments, lastTypedText)
            || !InstructionRequestsActingOnTypedResult(instruction, lastTypedText)
            || ReadString(call.Arguments, "condition") is "hidden" or "stable"
            || new[] { "nodeId", "automationId", "role", "type", "ancestorAutomationId", "action" }
                .Any(key => ReadString(call.Arguments, key) is not null))
        {
            return result;
        }

        var output = JsonNode.Parse(result.Output) as JsonObject;
        var content = output?["result"] as JsonObject ?? output;
        if (content?["matches"] is not JsonArray { Count: > 0 } matches
            || !matches.All(match => match?["role"]?.GetValue<string>() == "textbox"))
        {
            return result;
        }

        const string message = "The wait matched only the text input containing the query, not a search result. "
                               + "Wait for the exact result inside its observed result container, or use a discovered non-textbox result selector. Do not repeat this unqualified text wait.";
        output!["isError"] = true;
        content["satisfied"] = false;
        content["inputEchoOnly"] = true;
        content["message"] = message;
        return result with { IsError = true, Output = output.ToJsonString(), Message = message };
    }

    private static bool TryBuildTaskReassessmentGuardMessage(
        string manualToolName,
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks,
        IReadOnlySet<string> attemptedRepositoryTaskIds,
        IReadOnlySet<string> failedRepositoryTaskIds,
        out string message)
    {
        var unusedTasks = repositoryTasks
            .Where(task => !attemptedRepositoryTaskIds.Contains(task.TaskId))
            .ToArray();
        if (unusedTasks.Length == 0 && failedRepositoryTaskIds.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        message = $"Repository-task reassessment is required before manual UI tool '{manualToolName}'. "
                  + "Call the matching preloaded repository task when one covers the next residual transition. "
                  + $"If none applies, call {DeclareUncoveredStepToolName} with every remaining task ID before continuing manually."
                  + (unusedTasks.Length == 0
                      ? string.Empty
                      : $" Remaining task IDs: {string.Join(", ", unusedTasks.Select(task => task.TaskId))}.")
                  + (failedRepositoryTaskIds.Count == 0
                      ? string.Empty
                      : $" Failed task IDs requiring an explicit task-failed declaration: {string.Join(", ", failedRepositoryTaskIds.OrderBy(taskId => taskId, StringComparer.Ordinal))}.");
        return true;
    }

    private static ToolCallResult CreateUncoveredStepDeclarationResult(
        JsonObject arguments,
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks,
        IReadOnlySet<string> attemptedRepositoryTaskIds,
        IReadOnlySet<string> successfulRepositoryTaskIds,
        IReadOnlySet<string> failedRepositoryTaskIds)
    {
        var uncoveredStep = ReadString(arguments, "uncoveredStep");
        var reason = ReadString(arguments, "reason");
        var relatedTaskId = ReadString(arguments, "relatedTaskId");
        var evidence = ReadString(arguments, "evidence");
        var suppliedTaskIds = arguments["consideredTaskIds"] is JsonArray suppliedTaskIdValues
            ? suppliedTaskIdValues
                .OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var taskId) ? taskId.Trim() : string.Empty)
                .Where(taskId => taskId.Length > 0)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var knownTaskIds = repositoryTasks
            .Select(task => task.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        var unknownTaskIds = suppliedTaskIds
            .Where(taskId => !knownTaskIds.Contains(taskId))
            .OrderBy(taskId => taskId, StringComparer.Ordinal)
            .ToArray();
        var alreadyAttemptedTaskIds = suppliedTaskIds
            .Where(attemptedRepositoryTaskIds.Contains)
            .OrderBy(taskId => taskId, StringComparer.Ordinal)
            .ToArray();
        var missingTaskIds = repositoryTasks
            .Where(task => !attemptedRepositoryTaskIds.Contains(task.TaskId))
            .Select(task => task.TaskId)
            .Where(taskId => !suppliedTaskIds.Contains(taskId))
            .ToArray();

        if (uncoveredStep is null || reason is null || evidence is null)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                "uncoveredStep, reason, relatedTaskId, and evidence are required before manual UI can continue.");
        }
        if (reason is not ("no-matching-task"
            or "starting-state-not-satisfied"
            or "scope-mismatch"
            or "missing-input"
            or "partial-task-residual"
            or "task-failed"))
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"Unsupported uncovered-step reason '{reason}'.");
        }
        if (unknownTaskIds.Length > 0)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"The declaration included unknown task IDs: {string.Join(", ", unknownTaskIds)}.");
        }
        if (relatedTaskId is not null && !knownTaskIds.Contains(relatedTaskId))
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"The declaration referenced unknown related task ID '{relatedTaskId}'.");
        }
        if (alreadyAttemptedTaskIds.Length > 0)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"consideredTaskIds must contain only unused preloaded tasks. Already attempted task IDs: {string.Join(", ", alreadyAttemptedTaskIds)}.");
        }
        if (missingTaskIds.Length > 0)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"Reassess and include every unused preloaded task before manual UI. Missing task IDs: {string.Join(", ", missingTaskIds)}.");
        }

        var reasonValidationMessage = reason switch
        {
            "scope-mismatch" or "missing-input" when relatedTaskId is null
                || attemptedRepositoryTaskIds.Contains(relatedTaskId)
                || !suppliedTaskIds.Contains(relatedTaskId) =>
                "scope-mismatch and missing-input require the exact unused related task ID in consideredTaskIds and concrete evidence from its scope or required inputs.",
            "missing-input" when repositoryTasks.First(task => task.TaskId == relatedTaskId)
                .InputSchema["required"] is not JsonArray { Count: > 0 } =>
                "missing-input requires a task with declared required input properties; use scope-mismatch for an unrequested operation.",
            "no-matching-task" when relatedTaskId is not null =>
                "relatedTaskId must be null when reason is no-matching-task.",
            "starting-state-not-satisfied" when relatedTaskId is null =>
                "relatedTaskId must identify the unused task whose starting state is not satisfied.",
            "starting-state-not-satisfied" when attemptedRepositoryTaskIds.Contains(relatedTaskId) =>
                $"Task '{relatedTaskId}' was already attempted, so starting-state-not-satisfied is not valid.",
            "starting-state-not-satisfied" when !suppliedTaskIds.Contains(relatedTaskId) =>
                $"Task '{relatedTaskId}' must be included in consideredTaskIds.",
            "partial-task-residual" when relatedTaskId is null =>
                "relatedTaskId must identify the successfully completed partial task.",
            "partial-task-residual" when !successfulRepositoryTaskIds.Contains(relatedTaskId) =>
                $"Task '{relatedTaskId}' did not complete successfully, so partial-task-residual is not valid.",
            "task-failed" when relatedTaskId is null =>
                "relatedTaskId must identify the failed task.",
            "task-failed" when !failedRepositoryTaskIds.Contains(relatedTaskId) =>
                $"Task '{relatedTaskId}' has no failed attempt in this run.",
            _ => null
        };
        if (reasonValidationMessage is not null)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                reasonValidationMessage);
        }

        var strongMatches = repositoryTasks
            .Where(task => !attemptedRepositoryTaskIds.Contains(task.TaskId))
            .Where(task => suppliedTaskIds.Contains(task.TaskId))
            .Where(task => IsStrongRepositoryTaskMatch(task, uncoveredStep))
            .Select(task => task.TaskId)
            .OrderBy(taskId => taskId, StringComparer.Ordinal)
            .ToArray();
        var unaccountedStrongMatches = strongMatches
            .Where(taskId => reason is not ("starting-state-not-satisfied" or "scope-mismatch" or "missing-input")
                             || !string.Equals(taskId, relatedTaskId, StringComparison.Ordinal))
            .ToArray();
        var excludesRelatedTask = reason is "scope-mismatch" or "missing-input";
        if (unaccountedStrongMatches.Length > 0 && !excludesRelatedTask)
        {
            return CreateAgentGuardResult(
                "taskReassessmentDeclaration",
                $"The declared step strongly matches unused preloaded task(s): {string.Join(", ", unaccountedStrongMatches)}. "
                + "Call the matching typed task instead. Use starting-state-not-satisfied only with the exact blocked task ID and concrete observed evidence. "
                + "Use scope-mismatch when the task exceeds the instruction, or missing-input when a required value cannot be bound from the instruction or evidence. "
                + "Use task-failed or partial-task-residual only for a related task whose recorded outcome supports that reason.");
        }

        var manualUiAllowed = unaccountedStrongMatches.Length == 0;
        var message = manualUiAllowed
            ? $"Manual UI is allowed for the declared uncovered step '{uncoveredStep}' ({reason}). "
              + "Reassess remaining tasks after a navigation transition, not after text entry or a rejected action with performed=false. "
              + "A scope-mismatch or missing-input exclusion lasts for this instruction; omit that task from later declarations."
            : $"Excluded task '{relatedTaskId}' for this instruction ({reason}). "
              + $"Before manual UI, run or assess the remaining matching tasks: {string.Join(", ", unaccountedStrongMatches)}.";
        return new ToolCallResult(
            false,
            new JsonObject
            {
                ["isError"] = false,
                ["accepted"] = true,
                ["manualUiAllowed"] = manualUiAllowed,
                ["uncoveredStep"] = uncoveredStep,
                ["reason"] = reason,
                ["relatedTaskId"] = relatedTaskId,
                ["evidence"] = evidence,
                ["consideredTaskIds"] = new JsonArray(
                    suppliedTaskIds
                        .OrderBy(taskId => taskId, StringComparer.Ordinal)
                        .Select(taskId => (JsonNode?)JsonValue.Create(taskId))
                        .ToArray()),
                ["message"] = message
            }.ToJsonString(),
            message);
    }

    private static bool IsStrongRepositoryTaskMatch(
        RepositoryTaskShortcut task,
        string uncoveredStep)
    {
        var queryTerms = TokenizeTaskMatchText(uncoveredStep);
        if (queryTerms.Count == 0)
        {
            return false;
        }

        IEnumerable<string> inputPropertyNames = task.InputSchema["properties"] is JsonObject inputProperties
            ? inputProperties.Select(property => property.Key)
            : [];
        var taskTerms = TokenizeTaskMatchText(string.Join(
            ' ',
            task.TaskId,
            task.Title,
            task.Description,
            task.Feature ?? string.Empty,
            string.Join(' ', inputPropertyNames)));
        var matchedTermCount = queryTerms.Count(queryTerm => taskTerms.Any(
            taskTerm => AreStrongTaskTermsEquivalent(queryTerm, taskTerm)));
        return matchedTermCount >= 3
               && (double)matchedTermCount / queryTerms.Count >= 0.4;
    }

    private static IReadOnlyList<string> TokenizeTaskMatchText(string value)
        => Regex.Matches(value, @"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(term => !strongTaskMatchIgnoredWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool AreStrongTaskTermsEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var shorterLength = Math.Min(left.Length, right.Length);
        var longerLength = Math.Max(left.Length, right.Length);
        if (shorterLength >= 4
            && (left.StartsWith(right, StringComparison.Ordinal)
                || right.StartsWith(left, StringComparison.Ordinal))
            && (double)shorterLength / longerLength >= 0.7)
        {
            return true;
        }

        return strongTaskSynonymGroups.Any(group => group.Contains(left, StringComparer.Ordinal)
                                                    && group.Contains(right, StringComparer.Ordinal));
    }

    private static bool HasFocusedUiSelector(JsonObject arguments)
        => ReadString(arguments, "nodeId") is not null
           || ReadString(arguments, "automationId") is not null
           || ReadString(arguments, "text") is not null
           || ReadString(arguments, "role") is not null
           || ReadString(arguments, "type") is not null
           || ReadString(arguments, "ancestorAutomationId") is not null
           || ReadString(arguments, "action") is not null;

    private static string BuildAppGraphScrollScope(JsonObject arguments)
    {
        var scope = new JsonObject();
        foreach (var propertyName in new[]
                 {
                     "nodeId",
                     "automationId",
                     "text",
                     "role",
                     "type",
                     "ancestorAutomationId",
                     "action"
                 })
        {
            var value = NormalizeOptional(ReadString(arguments, propertyName));
            if (value is not null)
            {
                scope[propertyName] = value;
            }
        }

        scope["index"] = arguments["index"]?.DeepClone() ?? JsonValue.Create(0);
        scope["orientation"] = NormalizeOptional(ReadString(arguments, "orientation"))?.ToLowerInvariant()
                               ?? string.Empty;
        scope["direction"] = NormalizeOptional(ReadString(arguments, "direction"))?.ToLowerInvariant()
                             ?? string.Empty;
        if (scope.Count == 3)
        {
            scope["target"] = "viewport";
        }

        return scope.ToJsonString();
    }

    private static AppGraphStableActionScope? BuildAppGraphStableActionScope(
        string? currentDestinationId,
        OpenAiFunctionCall call)
    {
        var destinationId = NormalizeOptional(currentDestinationId);
        var automationId = ReadString(call.Arguments, "automationId");
        if (destinationId is null
            || automationId is null
            || !IsAppGraphNavigationAction(call.Name))
        {
            return null;
        }

        return new AppGraphStableActionScope(
            destinationId,
            call.Name,
            automationId,
            BuildAppGraphActionSelector(call.Arguments));
    }

    private static JsonObject BuildAppGraphActionSelector(JsonObject arguments)
    {
        var selector = new JsonObject();
        foreach (var propertyName in new[]
                 {
                     "nodeId",
                     "automationId",
                     "text",
                     "role",
                     "type",
                     "ancestorAutomationId",
                     "action",
                     "exact",
                     "targetX",
                     "targetY",
                     "screenX",
                     "screenY"
                 })
        {
            if (arguments[propertyName] is { } value)
            {
                selector[propertyName] = value.DeepClone();
            }
        }

        return selector;
    }

    private static bool TryBuildAppGraphScrollGuardMessage(
        string scrollScope,
        IReadOnlyDictionary<string, int> attemptsByScope,
        IReadOnlySet<string> terminalScopes,
        out string message)
    {
        if (terminalScopes.Contains(scrollScope))
        {
            message = "This App Graph scroll scope already reached an unchanged semantic viewport. Mark its scroll coverage complete or blocked and move to another safe branch.";
            return true;
        }

        var attempts = attemptsByScope.TryGetValue(scrollScope, out var count) ? count : 0;
        if (attempts >= MaximumAppGraphScrollAttemptsPerScope)
        {
            message = $"This App Graph scroll scope has reached its {MaximumAppGraphScrollAttemptsPerScope}-attempt limit. Record any remaining content as a blocked coverage gap and move to another safe branch.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    private static bool HasUnchangedSemanticViewport(string output)
    {
        try
        {
            var root = JsonNode.Parse(output) as JsonObject;
            var evidence = root?["result"]?["evidence"] as JsonObject;
            if (evidence?["before"] is not JsonObject before
                || evidence["after"] is not JsonObject after)
            {
                return false;
            }

            var beforeTreeHash = ReadString(before, "treeHash");
            var afterTreeHash = ReadString(after, "treeHash");
            return beforeTreeHash is not null
                   && afterTreeHash is not null
                   && string.Equals(beforeTreeHash, afterTreeHash, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAppGraphNavigationAction(string toolName)
        => toolName is "ansight_tap_ui"
            or "ansight_type_text"
            or "ansight_swipe_ui"
            or "ansight_pinch_ui"
            or "ansight_back_ui"
            or "ansight_run_ui_sequence"
            or "ansight_launch_app"
            or "ansight_terminate_app";

    private static ToolCallResult CreateAgentGuardResult(string guardName, string message)
        => new(
            true,
            new JsonObject
            {
                ["isError"] = true,
                [guardName] = true,
                ["message"] = message
            }.ToJsonString(),
            message);

    private sealed record AppGraphStableActionScope(
        string DestinationId,
        string ToolName,
        string AutomationId,
        JsonObject Selector);

    private static JsonObject SanitizeToolArguments(OpenAiFunctionCall call)
    {
        var safeArguments = call.Arguments.DeepClone().AsObject();
        if (string.Equals(call.Name, "ansight_type_text", StringComparison.Ordinal)
            && safeArguments["value"] is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            safeArguments["value"] = $"<redacted {text.Length} character(s)>";
        }

        return safeArguments;
    }

    private static string BuildToolCallSignature(OpenAiFunctionCall call)
    {
        var value = $"{call.Name}\n{call.Arguments.ToJsonString()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static string BuildModelPassCompletedMessage(
        int sequence,
        OpenAiTurn response,
        long durationMilliseconds)
    {
        var responseText = string.IsNullOrWhiteSpace(response.ResponseId)
            ? "no response id"
            : response.ResponseId;
        var transportText = response.Transport is { } transport
            ? $" Transport: {string.Join(", ", transport.Attempts.Select(static attempt => attempt.ReplayReason is null ? attempt.Mode : $"{attempt.Mode} ({attempt.ReplayReason})"))}; "
              + $"{transport.RequestBytes:N0} bytes sent; {transport.CompactionCount:N0} compaction(s)."
            : string.Empty;
        return $"Model pass {sequence} completed in {durationMilliseconds:N0} ms ({responseText}): "
               + $"{response.Tokens.InputTokens:N0} input, {response.Tokens.OutputTokens:N0} output, "
               + $"{response.Tokens.TotalTokens:N0} total, {response.Tokens.CachedInputTokens:N0} cached, "
               + $"{response.Tokens.CacheWriteInputTokens:N0} cache-write, "
               + $"{response.Tokens.ReasoningOutputTokens:N0} reasoning token(s), "
               + $"{response.FunctionCalls.Count:N0} function call(s).{transportText}";
    }

    private static SimulatorAgentAuditPayload CreateAuditPayload(
        string? content,
        int maximumCharacters,
        bool includeContent = true)
    {
        var normalized = content ?? string.Empty;
        var originalCharacterCount = normalized.Length;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        if (!includeContent)
        {
            return new SimulatorAgentAuditPayload(
                string.Empty,
                originalCharacterCount,
                originalCharacterCount > 0,
                hash);
        }

        if (normalized.Length <= maximumCharacters)
        {
            return new SimulatorAgentAuditPayload(normalized, originalCharacterCount, false, hash);
        }

        return new SimulatorAgentAuditPayload(
            normalized[..maximumCharacters],
            originalCharacterCount,
            true,
            hash);
    }

    private static SimulatorAgentOcrTraceEvidence? CreateOcrTraceEvidence(
        JsonObject? evidence,
        bool captureTrace)
    {
        if (!captureTrace || evidence is null)
        {
            return null;
        }

        var persistedEvidence = evidence.DeepClone().AsObject();
        var sourceScreenshotPath = ReadJsonString(persistedEvidence, "screenshotArtifactPath");
        persistedEvidence.Remove("screenshotArtifactPath");
        var capturedAtUtc = DateTimeOffset.TryParse(
            ReadJsonString(persistedEvidence, "capturedAtUtc"),
            out var parsedCapturedAtUtc)
            ? parsedCapturedAtUtc.ToUniversalTime()
            : DateTimeOffset.UtcNow;

        return new SimulatorAgentOcrTraceEvidence(
            ReadJsonString(persistedEvidence, "provider"),
            ReadJsonBoolean(persistedEvidence, "available"),
            ReadJsonString(persistedEvidence, "message"),
            capturedAtUtc,
            ReadJsonString(persistedEvidence, "screenshotFrameId"),
            ReadJsonString(persistedEvidence, "screenshotSha256"),
            ReadJsonString(persistedEvidence, "screenshotFormat"),
            ReadJsonInteger(persistedEvidence, "screenWidth"),
            ReadJsonInteger(persistedEvidence, "screenHeight"),
            ReadJsonInteger(persistedEvidence, "detectionCount"),
            CreateAuditPayload(
                persistedEvidence.ToJsonString(),
                MaximumAuditOcrResultCharacters))
        {
            SourceScreenshotPath = sourceScreenshotPath
        };
    }

    private static SimulatorAgentAccessibilityTraceEvidence? CreateAccessibilityTraceEvidence(
        JsonObject? evidence,
        bool captureTrace)
    {
        if (!captureTrace
            || evidence is null
            || evidence["snapshot"] is not JsonObject snapshot)
        {
            return null;
        }

        var capturedAtUtc = DateTimeOffset.TryParse(
            ReadJsonString(evidence, "capturedAtUtc"),
            out var parsedCapturedAtUtc)
            ? parsedCapturedAtUtc.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        return new SimulatorAgentAccessibilityTraceEvidence(
            ReadJsonString(evidence, "source") ?? "device.accessibility",
            capturedAtUtc,
            ReadJsonInteger(evidence, "nodeCount"),
            ReadJsonString(evidence, "snapshotId"),
            CreateAuditPayload(
                snapshot.ToJsonString(),
                MaximumAuditResultCharacters));
    }

    private static string? ReadJsonString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool ReadJsonBoolean(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<bool>(out var result)
           && result;

    private static int ReadJsonInteger(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<int>(out var result)
            ? result
            : 0;

    private sealed class OpenAiSimulatorAgentConversation
    {
        private readonly JsonArray history;
        private readonly JsonArray pendingInput;
        private readonly List<JsonObject> retainedToolCapabilities = [];
        private bool appendOnly;
        private int protectedHistoryPrefixLength = 1;

        public OpenAiSimulatorAgentConversation(JsonArray history, bool appendOnly, string initialReplayReason)
        {
            this.history = history ?? throw new ArgumentNullException(nameof(history));
            this.appendOnly = appendOnly;
            pendingInput = history.DeepClone().AsArray();
            retainedToolCapabilities.AddRange(history.OfType<JsonObject>()
                .Where(item => ReadString(item, "type") == "additional_tools")
                .Select(item => item.DeepClone().AsObject()));
            ReplayReason = initialReplayReason;
        }

        public JsonArray History => history;

        public JsonArray PendingInput => pendingInput;

        public bool RequiresFullReplay { get; private set; } = true;

        public string? ReplayReason { get; private set; }

        public void AcceptResponse(JsonArray output)
        {
            pendingInput.Clear();
            RequiresFullReplay = false;
            ReplayReason = null;
            var responseStartIndex = history.Count;
            foreach (var item in output)
            {
                history.Add(item?.DeepClone());
            }

            if (appendOnly)
            {
                // Only shorten the local recovery snapshot at a server-issued checkpoint.
                // The active response chain already contains it and must not be restarted.
                // Reprocessing an older checkpoint would duplicate capabilities added after it.
                for (var index = history.Count - 1; index >= responseStartIndex; index--)
                {
                    if (history[index] is JsonObject item && ReadString(item, "type") == "compaction")
                    {
                        for (var removed = 0; removed < index; removed++)
                        {
                            history.RemoveAt(0);
                        }
                        // A checkpoint summarizes conversation, not executable tool schemas.
                        // Restore declarations only in the recovery snapshot; the live chain
                        // already has them and pendingInput must remain incremental.
                        for (var capabilityIndex = 0; capabilityIndex < retainedToolCapabilities.Count; capabilityIndex++)
                        {
                            history.Insert(capabilityIndex, retainedToolCapabilities[capabilityIndex].DeepClone());
                        }
                        break;
                    }
                }
            }
            else
            {
                RequireReplayIf(TrimHistory(history, protectedHistoryPrefixLength), "history-trim");
            }
        }

        public void AddUserInput(string text)
        {
            var input = CreateUserInput(text);
            history.Add(input);
            pendingInput.Add(input.DeepClone());
            if (!appendOnly)
            {
                RequireReplayIf(TrimHistory(history, protectedHistoryPrefixLength), "history-trim");
            }
        }

        public void AddFunctionOutput(string callId, string output)
        {
            if (!appendOnly)
            {
                RequireReplayIf(CompactSupersededFunctionOutputs(history), "tool-output-compaction");
            }
            var input = CreateFunctionOutput(callId, output);
            history.Add(input);
            pendingInput.Add(input.DeepClone());
            if (!appendOnly)
            {
                RequireReplayIf(TrimHistory(history, protectedHistoryPrefixLength), "history-trim");
            }
        }

        public void UseHttpHistory()
        {
            appendOnly = false;
            // The fallback supplies the full catalogue at the request level. Avoid duplicate
            // declarations from the previous WebSocket conversation.
            for (var index = history.Count - 1; index >= 0; index--)
            {
                if (history[index] is JsonObject item && ReadString(item, "type") == "additional_tools")
                {
                    history.RemoveAt(index);
                }
            }
            // Guidance is sent once on the live chain, but must survive HTTP's item trimming
            // even when it was first observed late in the conversation or after a checkpoint.
            var retainedGuidance = retainedToolCapabilities
                .Where(item => ReadString(item, "type") != "additional_tools")
                .ToArray();
            foreach (var guidance in retainedGuidance)
            {
                for (var index = history.Count - 1; index >= 0; index--)
                {
                    if (JsonNode.DeepEquals(history[index], guidance))
                    {
                        history.RemoveAt(index);
                    }
                }
            }
            for (var index = 0; index < retainedGuidance.Length; index++)
            {
                history.Insert(index, retainedGuidance[index].DeepClone());
            }
            protectedHistoryPrefixLength = retainedGuidance.Length + 1;
            // Capability guidance can precede the checkpoint. Preserve that whole prefix so
            // legacy trimming cannot discard the compacted conversation during HTTP recovery.
            for (var index = history.Count - 1; index >= 0; index--)
            {
                if (history[index] is JsonObject item && ReadString(item, "type") == "compaction")
                {
                    protectedHistoryPrefixLength = index + 1;
                    break;
                }
            }
            RequireReplayIf(TrimHistory(history, protectedHistoryPrefixLength), "http-fallback");
        }

        public void AddToolCapabilities(JsonArray tools, string guidance)
        {
            AddCapability(AgentToolCatalog.CreateAdditionalToolsInput(tools));
            AddGuidance(guidance);
        }

        public void AddGuidance(string guidance)
        {
            if (string.IsNullOrWhiteSpace(guidance))
            {
                return;
            }
            AddCapability(new JsonObject
            {
                ["role"] = "developer",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = guidance })
            });
        }

        private void AddCapability(JsonObject item)
        {
            retainedToolCapabilities.Add(item.DeepClone().AsObject());
            if (appendOnly)
            {
                history.Add(item);
            }
            else
            {
                // Keep one-time instructions outside the mutable HTTP history tail.
                history.Insert(protectedHistoryPrefixLength, item);
                protectedHistoryPrefixLength++;
            }
            pendingInput.Add(item.DeepClone());
        }

        private void RequireReplayIf(bool changed, string reason)
        {
            if (changed)
            {
                RequiresFullReplay = true;
                ReplayReason = reason;
            }
        }
    }

    private static JsonObject CreateUserInput(string text)
    {
        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = text
                }
            }
        };
    }

    private static JsonObject CreateFunctionOutput(string callId, string output)
    {
        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = callId,
            ["output"] = output
        };
    }

    private static bool CompactSupersededFunctionOutputs(JsonArray history)
    {
        var rewritten = false;
        foreach (var item in history.OfType<JsonObject>())
        {
            if (!string.Equals(ReadString(item, "type"), "function_call_output", StringComparison.Ordinal)
                || item["output"] is not JsonValue outputValue
                || !outputValue.TryGetValue<string>(out var output)
                || string.IsNullOrEmpty(output))
            {
                continue;
            }

            JsonObject? parsedOutput = null;
            try
            {
                parsedOutput = JsonNode.Parse(output) as JsonObject;
            }
            catch (JsonException)
            {
                // Preserve a bounded prefix below when a tool returned non-JSON text.
            }

            if (parsedOutput?["superseded"] is JsonValue supersededValue
                && supersededValue.TryGetValue<bool>(out var superseded)
                && superseded)
            {
                continue;
            }

            var message = parsedOutput is null
                ? output
                : ReadString(parsedOutput, "message")
                  ?? (parsedOutput["result"] is JsonObject result
                      ? ReadString(result, "message")
                      : null)
                  ?? "Previous tool result superseded by a newer result.";
            if (message.Length > MaximumSupersededToolMessageCharacters)
            {
                message = $"{message[..(MaximumSupersededToolMessageCharacters - 1)]}…";
            }

            var isError = parsedOutput?["isError"] is JsonValue errorValue
                          && errorValue.TryGetValue<bool>(out var parsedError)
                          && parsedError;
            item["output"] = new JsonObject
            {
                ["isError"] = isError,
                ["superseded"] = true,
                ["message"] = message,
                ["originalCharacterCount"] = output.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)))
                    .ToLowerInvariant()
            }.ToJsonString();
            rewritten = true;
        }

        return rewritten;
    }

    private static bool TrimHistory(JsonArray history, int protectedPrefixLength)
    {
        var trimmed = false;
        while (history.Count > MaximumHistoryItems && history.Count > protectedPrefixLength)
        {
            RemoveOldestHistoryUnit(history, protectedPrefixLength);
            trimmed = true;
        }

        return trimmed;
    }

    private static void RemoveOldestHistoryUnit(JsonArray history, int oldestMutableIndex)
    {
        if (history.Count <= oldestMutableIndex)
        {
            return;
        }

        var oldest = history[oldestMutableIndex] as JsonObject;
        var oldestType = oldest is null ? null : ReadString(oldest, "type");
        var callId = oldest is null ? null : ReadString(oldest, "call_id");
        history.RemoveAt(oldestMutableIndex);

        if (callId is null
            || (oldestType is not "function_call" and not "function_call_output"))
        {
            return;
        }

        var counterpartType = oldestType == "function_call"
            ? "function_call_output"
            : "function_call";
        for (var index = oldestMutableIndex; index < history.Count; index++)
        {
            if (history[index] is JsonObject candidate
                && string.Equals(ReadString(candidate, "type"), counterpartType, StringComparison.Ordinal)
                && string.Equals(ReadString(candidate, "call_id"), callId, StringComparison.Ordinal))
            {
                history.RemoveAt(index);
                return;
            }
        }
    }

    private static JsonObject BuildReportAppGraphProgressToolDefinition()
    {
        var destinationProperties = new JsonObject
        {
            ["id"] = StringSchema("Stable canonical destination ID for this run."),
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("screen", "dialog", "state")
            },
            ["name"] = StringSchema("Canonical user-facing destination name."),
            ["parentScreen"] = StringSchema("Containing destination ID for an internal state or structurally grouped host child; otherwise an empty string. Host and tab-group reports also derive this relationship."),
            ["synonyms"] = StringArraySchema("Evidence-backed alternative names."),
            ["purpose"] = StringSchema("What the user can accomplish at this destination."),
            ["description"] = StringSchema("Concise observable evidence for this destination."),
            ["scrollStatus"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Destination scroll classification. unknown and in_progress remain on the host frontier; not_scrollable, complete, and blocked are terminal.",
                ["enum"] = new JsonArray("unknown", "not_scrollable", "in_progress", "complete", "blocked")
            },
            ["confidence"] = ConfidenceSchema()
        };
        var transitionProperties = new JsonObject
        {
            ["id"] = StringSchema("Stable canonical transition ID for this run."),
            ["from"] = StringSchema("Source destination ID."),
            ["to"] = StringSchema("Resulting destination ID."),
            ["automationId"] = StringSchema("Observed stable automation ID, or an empty string when the gap must remain visible."),
            ["semanticMeaning"] = StringSchema("What using the interacted element means to the user."),
            ["confidence"] = ConfidenceSchema()
        };
        var navigationTechnologyProperties = new JsonObject
        {
            ["framework"] = StringSchema("Exact framework selected by ansight_get_live_navigation_structure."),
            ["kind"] = StringSchema("Framework-owned topology kind from the selected controller's technologyKinds list."),
            ["navigationToolId"] = StringSchema("Exact navigation-state tool ID that supplied the structure, or an empty string only when framework identity came from an exact framework visual tree."),
            ["structureFingerprint"] = StringSchema("SHA-256 structureFingerprint returned with the navigation observation, or an empty string for an exact visual-tree fallback.")
        };
        var navigationHostProperties = new JsonObject
        {
            ["id"] = StringSchema("Stable canonical navigation-host ID for this run."),
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    "flyout",
                    "drawer",
                    "bottom_tabs",
                    "top_tabs",
                    "navigation_rail",
                    "shell",
                    "other")
            },
            ["name"] = StringSchema("Canonical user-facing name for the persistent navigation host."),
            ["destinationId"] = StringSchema("Destination ID representing the persistent host itself."),
            ["activeChildDestinationId"] = StringSchema("Currently selected child destination ID."),
            ["childDestinationIds"] = StringArraySchema("Every ordered child destination exposed by the host, including the active child."),
            ["framework"] = StringSchema("Compatibility projection of technology.framework."),
            ["technology"] = StrictObjectSchema(navigationTechnologyProperties.DeepClone().AsObject()),
            ["confidence"] = ConfidenceSchema()
        };
        var tabGroupProperties = new JsonObject
        {
            ["id"] = StringSchema("Stable canonical internal-tab-group ID for this run."),
            ["parentDestinationId"] = StringSchema("Containing screen destination ID."),
            ["selectedDestinationId"] = StringSchema("Tab destination selected when this group was first observed. Keep this initial value on later reports."),
            ["tabDestinationIds"] = StringArraySchema("Every tab destination in visible order, including the selected tab before any tab action."),
            ["technology"] = StrictObjectSchema(navigationTechnologyProperties.DeepClone().AsObject()),
            ["confidence"] = ConfidenceSchema()
        };
        var actionProperties = new JsonObject
        {
            ["id"] = StringSchema("Stable canonical action candidate ID for this run."),
            ["destinationId"] = StringSchema("Canonical source destination ID where the action was observed."),
            ["toolName"] = StringSchema("Exact Ansight action tool used or intended for this candidate."),
            ["automationId"] = StringSchema("Observed stable automation ID, or an empty string only for a terminal unavailable or blocked candidate."),
            ["semanticMeaning"] = StringSchema("What using this action means to the user."),
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    "queued",
                    "attempted",
                    "explored",
                    "blocked",
                    "unsafe",
                    "unavailable")
            },
            ["lastOutcome"] = StringSchema("Latest evidence-backed outcome, or an empty string before the first attempt."),
            ["resultDestinationId"] = StringSchema("Verified destination ID reached by this action, or an empty string when not established.")
        };
        var coverageProperties = new JsonObject
        {
            ["safeActionsObserved"] = NonNegativeIntegerSchema(),
            ["actionsExplored"] = NonNegativeIntegerSchema(),
            ["scrollContainersObserved"] = NonNegativeIntegerSchema(),
            ["scrollContainersCompleted"] = NonNegativeIntegerSchema(),
            ["gaps"] = StringArraySchema("Known inaccessible branches, missing automation IDs, or blocked scroll coverage.")
        };
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = ReportAppGraphProgressToolName,
            ["description"] = "Publish verified incremental App Graph destinations, transitions, host-tracked action candidates, and scroll coverage. The result returns exact pending frontier actions. This does not complete exploration.",
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["message"] = StringSchema("Short description of the latest verified exploration progress."),
                    ["currentDestinationId"] = StringSchema("Current destination ID, or an empty string when unknown."),
                    ["destinations"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "New or updated verified destinations; use an empty array when only coverage changed.",
                        ["items"] = StrictObjectSchema(destinationProperties)
                    },
                    ["transitions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "New or updated verified transitions; use an empty array when only a destination or coverage changed.",
                        ["items"] = StrictObjectSchema(transitionProperties)
                    },
                    ["navigationHosts"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "New or updated persistent navigation hosts. Use an empty array only when no host structure changed.",
                        ["items"] = StrictObjectSchema(navigationHostProperties)
                    },
                    ["tabGroups"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "New or updated internal tab inventories. Create the selected tab destination before navigating to sibling tabs.",
                        ["items"] = StrictObjectSchema(tabGroupProperties)
                    },
                    ["actions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "New or updated action candidates for observed destinations. Reuse existing IDs and terminalize blocked, unsafe, or unrepresented branches instead of leaving them queued.",
                        ["items"] = StrictObjectSchema(actionProperties)
                    },
                    ["coverage"] = StrictObjectSchema(coverageProperties)
                },
                ["required"] = new JsonArray(
                    "message",
                    "currentDestinationId",
                    "destinations",
                    "transitions",
                    "navigationHosts",
                    "tabGroups",
                    "actions",
                    "coverage"),
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject StrictObjectSchema(JsonObject properties)
        => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(static property => (JsonNode?)property.Key).ToArray()),
            ["additionalProperties"] = false
        };

    private static JsonObject StringSchema(string description)
        => new()
        {
            ["type"] = "string",
            ["description"] = description
        };

    private static JsonObject StringArraySchema(string description)
        => new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" }
        };

    private static JsonObject ConfidenceSchema()
        => new()
        {
            ["type"] = "number",
            ["minimum"] = 0,
            ["maximum"] = 1
        };

    private static JsonObject NonNegativeIntegerSchema()
        => new()
        {
            ["type"] = "integer",
            ["minimum"] = 0
        };

    private static JsonObject BuildDeclareUncoveredStepToolDefinition(
        IReadOnlyList<RepositoryTaskShortcut> repositoryTasks)
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = DeclareUncoveredStepToolName,
            ["description"] = "Declare an uncovered manual step using observed evidence. Reassess after navigation; task outcomes and exclusions are validated by the host.",
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["uncoveredStep"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The residual UI step."
                    },
                    ["reason"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Choose the observed cause. partial-task-residual requires a passed task; task-failed requires a failed task. Scope/input exclusions persist.",
                        ["enum"] = new JsonArray(
                            "no-matching-task",
                            "starting-state-not-satisfied",
                            "scope-mismatch",
                            "missing-input",
                            "partial-task-residual",
                            "task-failed")
                    },
                    ["relatedTaskId"] = new JsonObject
                    {
                        ["type"] = new JsonArray("string", "null"),
                        ["description"] = "Exact related task ID; null only for no-matching-task."
                    },
                    ["evidence"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["description"] = "Observed UI/result or the extra operation/missing input."
                    },
                    ["consideredTaskIds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "All unused task IDs reassessed. Omit attempted/excluded tasks; [] when none remain.",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    }
                },
                ["required"] = new JsonArray(
                    "uncoveredStep",
                    "reason",
                    "relatedTaskId",
                    "evidence",
                    "consideredTaskIds"),
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject BuildCompleteInstructionToolDefinition()
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = CompleteInstructionToolName,
            ["description"] = "Finish the current instruction after verifying its outcome.",
            ["strict"] = true,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["outcome"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("succeeded", "failed")
                    },
                    ["summary"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Concise, evidence-grounded outcome summary."
                    }
                },
                ["required"] = new JsonArray("outcome", "summary"),
                ["additionalProperties"] = false
            }
        };
    }

    private static SimulatorAgentCompletion ParseCompletion(JsonObject arguments)
    {
        var outcome = ReadString(arguments, "outcome");
        var summary = ReadString(arguments, "summary") ?? "The model did not provide an outcome summary.";
        var succeeded = string.Equals(outcome, "succeeded", StringComparison.OrdinalIgnoreCase);
        var normalizedOutcome = succeeded ? "succeeded" : "failed";
        return new SimulatorAgentCompletion(
            succeeded,
            summary,
            new JsonObject
            {
                ["accepted"] = true,
                ["outcome"] = normalizedOutcome
            }.ToJsonString());
    }

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", parameterName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SimulatorAgentRunResult BuildResult(
        SimulatorAgentRunStatus status,
        string message,
        IReadOnlyList<SimulatorAgentInstructionResult> instructions,
        int totalTurns,
        int totalToolCalls,
        SimulatorAgentTokenUsage tokenUsage,
        TimeSpan duration,
        SimulatorAgentRunAudit audit,
        AuditSaveResult saveResult)
    {
        return new SimulatorAgentRunResult(
            status,
            message,
            instructions.ToArray(),
            totalTurns,
            totalToolCalls,
            tokenUsage.InputTokens,
            tokenUsage.OutputTokens,
            duration,
            audit,
            saveResult.FilePath,
            saveResult.ErrorMessage);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record SimulatorAgentCompletion(bool Succeeded, string Summary, string Output);
}
