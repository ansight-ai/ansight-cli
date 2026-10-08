using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;
using Ansight.Host.Runtime;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.SimulatorAgent.Tools;

internal sealed class ToolGateway : IToolGateway
{
    private const string TypeSecretToolName = "ansight_type_secret";
    private const string RepositoryTaskToolPrefix = "ansight_task_";
    private const int MaximumPreloadedRepositoryTasks = 5;
    private const int MaximumFocusedRepositoryTaskQueries = 8;
    private const int MaximumRepositoryTasksPerFocusedQuery = 4;
    private const double MinimumPreloadedRepositoryTaskScore = 65;
    private const double MinimumPreloadedRepositoryTaskCoverage = 0.5;
    private const int MaximumRepositoryTaskToolNameLength = 64;
    private const int MaximumToolOutputCharacters = 12_000;
    private const int MaximumTruncatedToolPrefixCharacters = 4_000;
    private static readonly HashSet<string> ActionToolNames = new(StringComparer.Ordinal)
    {
        "ansight_tap_ui",
        "ansight_type_text",
        "ansight_swipe_ui",
        "ansight_scroll_ui",
        "ansight_pinch_ui",
        "ansight_back_ui",
        "ansight_run_ui_sequence"
    };
    private static readonly HashSet<string> AllowedToolNames = new(StringComparer.Ordinal)
    {
        "ansight_take_screenshot",
        "ansight_get_live_visual_tree",
        "ansight_get_live_navigation_structure",
        "ansight_find_ui",
        "ansight_scan_screen",
        "ansight_wait_for_ui",
        "ansight_assert_ui",
        "ansight_tap_ui",
        "ansight_type_text",
        "ansight_swipe_ui",
        "ansight_scroll_ui",
        "ansight_pinch_ui",
        "ansight_back_ui",
        "ansight_run_ui_sequence",
        "ansight_list_host_devices",
        "ansight_start_device",
        "ansight_launch_app",
        "ansight_terminate_app",
        "ansight_list_tasks",
        "ansight_describe_module",
        "ansight_run_task",
        "ansight_list_app_tools",
        "ansight_call_app_tool",
        "ansight_get_execution_capabilities", "ansight_require_capabilities",
        "ansight_list_sandbox_file", "ansight_read_sandbox_file", "ansight_capture_sandbox_file",
        "ansight_get_telemetry", "ansight_get_logs"
    };

    private readonly IOperationDispatcher operations;
    private readonly Lock appToolCatalogGate = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, AppToolPermission>>
        appToolPermissionsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<DiscoveryCacheKey, ToolCallResult>
        appToolDiscoveryCache = [];
    private readonly AsyncLocal<SimulatorAgentToolRunState?> currentRun = new();

    public ToolGateway(IOperationDispatcher operations)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public void BeginRun(
        string sessionId,
        SecretAccess secretAccess,
        string? targetDeviceIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(secretAccess);
        if (currentRun.Value is not null)
        {
            throw new InvalidOperationException("A simulator agent run is already active.");
        }

        var captureScope = operations.BeginSimulatorAgentRun(sessionId.Trim(), targetDeviceIdentifier);
        currentRun.Value = new SimulatorAgentToolRunState(secretAccess, captureScope);
        try
        {
            lock (appToolCatalogGate)
            {
                var normalizedSessionId = sessionId.Trim();
                appToolPermissionsBySession.Remove(normalizedSessionId);
                foreach (var cacheKey in appToolDiscoveryCache.Keys
                             .Where(key => string.Equals(key.SessionId, normalizedSessionId, StringComparison.Ordinal))
                             .ToArray())
                {
                    appToolDiscoveryCache.Remove(cacheKey);
                }
            }
        }
        catch
        {
            currentRun.Value = null;
            captureScope.Dispose();
            throw;
        }
    }

    public void EndRun()
    {
        var run = currentRun.Value;
        currentRun.Value = null;
        run?.CaptureScope.Dispose();
    }

    public void RebindRun(
        string sessionId,
        string? targetDeviceIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var run = currentRun.Value;
        if (run is null)
        {
            throw new InvalidOperationException("A simulator agent run is not active.");
        }

        var replacementScope = operations.BeginSimulatorAgentRun(
            sessionId.Trim(),
            targetDeviceIdentifier);
        var previousScope = run.CaptureScope;
        run.CaptureScope = replacementScope;
        run.UiContext.Invalidate();
        previousScope.Dispose();

        lock (appToolCatalogGate)
        {
            var normalizedSessionId = sessionId.Trim();
            appToolPermissionsBySession.Remove(normalizedSessionId);
            foreach (var cacheKey in appToolDiscoveryCache.Keys
                         .Where(key => string.Equals(key.SessionId, normalizedSessionId, StringComparison.Ordinal))
                         .ToArray())
            {
                appToolDiscoveryCache.Remove(cacheKey);
            }
        }
    }

    public JsonArray BuildOpenAiToolDefinitions(
        IReadOnlyList<RepositoryTaskShortcut>? repositoryTasks = null,
        SessionCapabilities? capabilities = null,
        bool appGraphEnabled = false)
    {
        capabilities ??= SessionCapabilities.Empty();
        var definitions = operations.BuildToolsListResult()["tools"] as JsonArray ?? [];
        var tools = new JsonArray();
        var run = currentRun.Value;
        run?.RepositoryTaskIdsByToolName.Clear();
        foreach (var task in (repositoryTasks ?? []).OrderBy(static task => task.TaskId, StringComparer.Ordinal))
        {
            if (run is not null)
            {
                run.RepositoryTaskIdsByToolName[task.ToolName] = task.TaskId;
            }
        }
        foreach (var definition in definitions.OfType<JsonObject>()
                     .OrderBy(static definition => ReadString(definition, "name"), StringComparer.Ordinal))
        {
            var name = ReadString(definition, "name");
            if (name is null || !AllowedToolNames.Contains(name))
            {
                continue;
            }
            if (capabilities.IsDeviceOnly && !DeviceExecutionTools.IsSupported(name)) continue;
            if (string.Equals(name, "ansight_get_live_navigation_structure", StringComparison.Ordinal)
                && capabilities.NavigationFrameworks.Count == 0)
            {
                continue;
            }

            var parameters = (definition["inputSchema"]?.DeepClone() as JsonObject) ?? new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            };
            RemoveHostControlledTargetProperties(parameters);
            if (string.Equals(name, "ansight_find_ui", StringComparison.Ordinal))
            {
                RemoveParameter(parameters, "index");
            }
            else if (string.Equals(name, "ansight_get_live_visual_tree", StringComparison.Ordinal))
            {
                ConfigureVisualTreeParameters(parameters, capabilities);
            }
            else if (string.Equals(name, "ansight_get_live_navigation_structure", StringComparison.Ordinal))
            {
                ConfigureNavigationParameters(parameters, capabilities);
            }
            else if (string.Equals(name, "ansight_list_tasks", StringComparison.Ordinal))
            {
                // The run's --trace setting controls discovery diagnostics; keep inventories
                // and rejected candidates out of ordinary model tool responses.
                RemoveParameter(parameters, "includeDiagnostics");
            }
            ProjectModelParameters(name, parameters);
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = BuildToolDescription(
                    name,
                    ReadString(definition, "description") ?? name,
                    capabilities,
                    appGraphEnabled),
                ["parameters"] = parameters
            });
        }

        if (run?.SecretAccess.Secrets.Count > 0)
        {
            var typeTextDefinition = definitions.OfType<JsonObject>().FirstOrDefault(definition => string.Equals(
                ReadString(definition, "name"),
                "ansight_type_text",
                StringComparison.Ordinal));
            tools.Add(BuildTypeSecretToolDefinition(typeTextDefinition, run.SecretAccess));
        }

        foreach (var task in (repositoryTasks ?? []).OrderBy(static task => task.TaskId, StringComparer.Ordinal))
        {
            tools.Add(BuildRepositoryTaskToolDefinition(task));
        }

        return tools;
    }

    public async Task<IReadOnlyList<RepositoryTaskShortcut>> GetRepositoryTaskShortcutsAsync(
        string sessionId,
        string instruction,
        CancellationToken cancellationToken,
        Action<SimulatorAgentRepositoryTaskDiscoveryTrace>? trace = null,
        IReadOnlyList<string>? preferredTaskIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedInstruction = instruction.Trim();
        var queries = new List<string> { normalizedInstruction };
        queries.AddRange(BuildFocusedRepositoryTaskQueries(normalizedInstruction));
        var candidatesByTaskId = new Dictionary<string, RepositoryTaskShortcut>(StringComparer.Ordinal);
        var focusedLeaders = new List<string>();

        for (var queryIndex = 0; queryIndex < queries.Count; queryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new JsonObject
            {
                ["sessionId"] = sessionId.Trim(),
                ["query"] = queries[queryIndex],
                ["maxResults"] = queryIndex == 0
                    ? MaximumPreloadedRepositoryTasks
                    : MaximumRepositoryTasksPerFocusedQuery
            };
            if (trace is not null)
            {
                arguments["includeDiagnostics"] = true;
            }
            var result = await operations.CallToolAsync(
                    "ansight_list_tasks",
                    arguments,
                    RunRequestContext.CreateCorrelationId(allowScreenshotOcr: false))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsError)
            {
                trace?.Invoke(new SimulatorAgentRepositoryTaskDiscoveryTrace("query", queries[queryIndex], [])
                {
                    Message = "Repository task discovery failed; no candidates were accepted from this query."
                });
                continue;
            }

            var returnedTasks = ReadRepositoryTaskShortcuts(result);
            var queryTasks = returnedTasks
                .Where(task => !IsRepositoryTaskExplicitlyExcluded(normalizedInstruction, task.TaskId))
                .Where(task => IsRelevantRepositoryTaskShortcut(task, normalizedInstruction))
                .ToArray();
            var decisions = trace is null ? null : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (decisions is not null)
            {
                foreach (var task in returnedTasks)
                {
                    var reasons = new List<string>();
                    if (IsRepositoryTaskExplicitlyExcluded(normalizedInstruction, task.TaskId))
                    {
                        reasons.Add("explicit-exclusion");
                    }
                    if (task.Score is null || task.Score < MinimumPreloadedRepositoryTaskScore)
                    {
                        reasons.Add(task.Score is null ? "missing-score" : "below-minimum-score");
                    }
                    if (task.Coverage is null || task.Coverage < MinimumPreloadedRepositoryTaskCoverage)
                    {
                        reasons.Add(task.Coverage is null ? "missing-coverage" : "below-minimum-coverage");
                    }
                    if (!RepositoryTaskSearchMatcher.HasTaskIntentMatch(task.TaskId, task.Title, task.Feature, normalizedInstruction, task.Keywords))
                    {
                        reasons.Add("task-intent-mismatch");
                    }
                    decisions[task.TaskId] = reasons;
                }
            }
            if (queryIndex > 0 && queryTasks.Length > 0)
            {
                focusedLeaders.Add(queryTasks[0].TaskId);
            }

            foreach (var task in queryTasks)
            {
                var isDuplicate = candidatesByTaskId.TryGetValue(task.TaskId, out var existing);
                var improvesMatch = !isDuplicate || CompareRepositoryTaskMatches(task, existing!) > 0;
                if (improvesMatch)
                {
                    candidatesByTaskId[task.TaskId] = task;
                }
                if (decisions is not null)
                {
                    decisions[task.TaskId] = [isDuplicate
                        ? improvesMatch ? "duplicate-better-match-retained" : "duplicate-earlier-match-retained"
                        : "preload-candidate"];
                }
            }
            if (trace is not null)
            {
                trace(ReadRepositoryTaskDiscoveryTrace(result, queries[queryIndex], returnedTasks, decisions!));
            }
        }

        // Draft test runs explicitly chose these tasks. Search by exact ID and preload
        // them even when a short task covers too little of the whole journey to pass
        // the normal relevance thresholds.
        var preferredTasks = new List<RepositoryTaskShortcut>();
        foreach (var taskId in (preferredTaskIds ?? [])
                     .Where(static id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal)
                     .Take(MaximumPreloadedRepositoryTasks))
        {
            if (candidatesByTaskId.TryGetValue(taskId, out var candidate))
            {
                preferredTasks.Add(candidate);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await operations.CallToolAsync(
                    "ansight_list_tasks",
                    new JsonObject
                    {
                        ["sessionId"] = sessionId.Trim(),
                        ["query"] = taskId,
                        ["maxResults"] = RepositoryTaskProtocol.MaximumDiscoveryResults
                    },
                    RunRequestContext.CreateCorrelationId(allowScreenshotOcr: false))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var exactTask = result.IsError ? null : ReadRepositoryTaskShortcuts(result)
                .FirstOrDefault(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
            if (exactTask is not null) preferredTasks.Add(exactTask);
            trace?.Invoke(new SimulatorAgentRepositoryTaskDiscoveryTrace("preferred", taskId, [])
            {
                SelectedTaskIds = exactTask is null ? [] : [taskId],
                Message = exactTask is null
                    ? "The selected draft task was not found in this run's task catalog."
                    : "The selected draft task was preloaded by exact ID."
            });
        }

        var selectedTasks = new List<RepositoryTaskShortcut>();
        var selectedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in preferredTasks)
        {
            if (selectedTaskIds.Add(task.TaskId)) selectedTasks.Add(task);
        }
        foreach (var taskId in focusedLeaders)
        {
            if (selectedTaskIds.Add(taskId)
                && candidatesByTaskId.TryGetValue(taskId, out var task))
            {
                selectedTasks.Add(task);
            }
        }

        foreach (var task in candidatesByTaskId.Values
                     .OrderByDescending(task => task.Score ?? 0)
                     .ThenByDescending(task => task.Coverage ?? 0)
                     .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(task => task.TaskId, StringComparer.Ordinal))
        {
            if (selectedTaskIds.Add(task.TaskId))
            {
                selectedTasks.Add(task);
            }
        }

        var shortcuts = selectedTasks
            .Take(MaximumPreloadedRepositoryTasks)
            .Select(task => task with
            {
                ToolName = BuildRepositoryTaskToolName(task.TaskId)
            })
            .ToArray();
        trace?.Invoke(new SimulatorAgentRepositoryTaskDiscoveryTrace(
            "selection",
            null,
            selectedTasks.Select((task, index) => new SimulatorAgentRepositoryTaskCandidateTrace(
                task.TaskId,
                task.Score,
                task.Coverage,
                [index < MaximumPreloadedRepositoryTasks ? "selected" : "preload-task-limit"])).ToArray())
        {
            SelectedTaskIds = shortcuts.Select(task => task.TaskId).ToArray(),
            Message = $"Preload thresholds: score >= {MinimumPreloadedRepositoryTaskScore}, coverage >= {MinimumPreloadedRepositoryTaskCoverage}; maximum {MaximumPreloadedRepositoryTasks} shortcuts."
        });
        return shortcuts;
    }

    private static SimulatorAgentRepositoryTaskDiscoveryTrace ReadRepositoryTaskDiscoveryTrace(
        RequestResult result,
        string query,
        IReadOnlyList<RepositoryTaskShortcut> returnedTasks,
        IReadOnlyDictionary<string, IReadOnlyList<string>> decisions)
    {
        var content = result.Payload?["structuredContent"] ?? result.Payload;
        var diagnostics = content?["diagnostics"] as JsonObject;
        var candidates = diagnostics?["candidates"] is JsonArray values
            ? values.OfType<JsonObject>().Select(candidate =>
            {
                var taskId = ReadString(candidate, "taskId") ?? string.Empty;
                return new SimulatorAgentRepositoryTaskCandidateTrace(
                    taskId,
                    ReadDouble(candidate, "score"),
                    ReadDouble(candidate, "coverage"),
                    decisions.TryGetValue(taskId, out var reasons)
                        ? reasons
                        : [ReadString(candidate, "reason") ?? "unknown"])
                {
                    MatchedQueryTerms = ReadDiagnosticStrings(candidate["matchedQueryTerms"]),
                    UnmatchedQueryTerms = ReadDiagnosticStrings(candidate["unmatchedQueryTerms"])
                };
            }).ToArray()
            : returnedTasks.Select(task => new SimulatorAgentRepositoryTaskCandidateTrace(
                task.TaskId, task.Score, task.Coverage, decisions[task.TaskId])).ToArray();
        return new SimulatorAgentRepositoryTaskDiscoveryTrace("query", query, candidates)
        {
            AvailableTaskCount = diagnostics?["availableTaskCount"]?.GetValue<int>(),
            AvailableTaskIds = ReadDiagnosticStrings(diagnostics?["availableTaskIds"]),
            DiagnosticsTruncated = diagnostics?["truncated"]?.GetValue<bool>() == true,
            Message = diagnostics is null
                ? "The task provider did not return inventory diagnostics; only returned candidates are shown."
                : null
        };
    }

    private static IReadOnlyList<string> ReadDiagnosticStrings(JsonNode? value)
        => value is JsonArray values
            ? values.OfType<JsonValue>()
                .Select(item => item.TryGetValue<string>(out var text) ? text : null)
                .OfType<string>()
                .ToArray()
            : [];

    private static IReadOnlyList<RepositoryTaskShortcut> ReadRepositoryTaskShortcuts(
        RequestResult result)
    {
        try
        {
            var content = result.Payload?["structuredContent"] ?? result.Payload;
            var tasks = content?["tasks"] as JsonArray;
            return tasks?
                       .OfType<JsonObject>()
                       .Select(CreateRepositoryTaskShortcut)
                       .OfType<RepositoryTaskShortcut>()
                       .ToArray()
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> BuildFocusedRepositoryTaskQueries(string instruction)
    {
        const string clauseSeparators = @"(?i)(?:\bthen\b|\bbut\b|,\s*(?=(?:search|find|open|select|tap|return|navigate|verify|use)\b))";
        var queries = new List<string>();
        var sentences = new List<string>();
        foreach (var sentence in Regex.Split(instruction, @"[.!?;\r\n]+", RegexOptions.CultureInvariant))
        {
            if (!ContainsNegativeTaskDirective(sentence))
            {
                sentences.Add(sentence);
                continue;
            }

            // A negative directive may share a sentence with requested transitions. Keep
            // the positive neighbors, including a location followed by its search action.
            foreach (var segment in Regex.Split(
                sentence,
                @"(?i)(?:\bthen\b|\bbut\b|,\s*(?=(?:do\s+not|don['\u2019]?t|never|must\s+not)\b))",
                RegexOptions.CultureInvariant))
            {
                if (!ContainsNegativeTaskDirective(segment))
                {
                    sentences.Add(segment);
                }
                else
                {
                    sentences.AddRange(Regex.Split(segment, clauseSeparators, RegexOptions.CultureInvariant)
                        .Where(clause => !ContainsNegativeTaskDirective(clause)));
                }
            }
        }
        foreach (var sentence in sentences)
        {
            // An explicitly named task late in a long instruction must not lose its query
            // budget to clause variants of an earlier transition.
            queries.AddRange(Regex.Matches(
                    sentence,
                    @"(?i)\b(?:run|running|use|using|invoke|call)\s+[`'""\s]*(?<task>[a-z][a-z0-9]*(?:[-_][a-z0-9]+)+)",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Groups["task"].Value));
        }

        queries.AddRange(sentences);
        foreach (var sentence in sentences)
        {
            // Preserve the location with its action before splitting into smaller transitions:
            // "On the map page, search for ..." is one useful map-search intent.
            queries.AddRange(Regex.Split(
                sentence,
                clauseSeparators,
                RegexOptions.CultureInvariant));
        }

        return queries
            .Select(segment => segment.Trim(' ', '\t', ',', ':', '-', '`', '\'', '"'))
            .Where(segment => segment.Length >= 3)
            .Where(segment => !string.Equals(segment, instruction, StringComparison.OrdinalIgnoreCase))
            .Where(segment => !ContainsNegativeTaskDirective(segment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumFocusedRepositoryTaskQueries)
            .ToArray();
    }

    private static bool ContainsNegativeTaskDirective(string value)
        => Regex.IsMatch(
            value,
            @"(?i)\b(?:do\s+not|don['\u2019]?t|never|must\s+not)\b",
            RegexOptions.CultureInvariant);

    private static bool IsRepositoryTaskExplicitlyExcluded(string instruction, string taskId)
        => Regex.IsMatch(
            instruction,
            $"(?i)\\b(?:do\\s+not|don['\\u2019]?t|never|must\\s+not)\\s+(?:use|run|invoke|call)\\s+[`'\"]?{Regex.Escape(taskId)}(?:[`'\"]|\\b)",
            RegexOptions.CultureInvariant);

    private static int CompareRepositoryTaskMatches(
        RepositoryTaskShortcut left,
        RepositoryTaskShortcut right)
    {
        var scoreComparison = Nullable.Compare(left.Score, right.Score);
        return scoreComparison != 0
            ? scoreComparison
            : Nullable.Compare(left.Coverage, right.Coverage);
    }

    private static bool IsRelevantRepositoryTaskShortcut(
        RepositoryTaskShortcut task,
        string instruction)
        => task.Score >= MinimumPreloadedRepositoryTaskScore
           && task.Coverage >= MinimumPreloadedRepositoryTaskCoverage
           && RepositoryTaskSearchMatcher.HasTaskIntentMatch(
               task.TaskId, task.Title, task.Feature, instruction, task.Keywords);

    public OpenAiFunctionCall NormalizeFunctionCall(
        OpenAiFunctionCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        string? taskId = null;
        currentRun.Value?.RepositoryTaskIdsByToolName.TryGetValue(call.Name, out taskId);

        if (taskId is null)
        {
            if (string.Equals(call.Name, "ansight_run_task", StringComparison.Ordinal)
                && ReadString(call.Arguments, "taskId") is { } suppliedTaskId
                && currentRun.Value?.RepositoryTaskIdsByToolName.TryGetValue(suppliedTaskId, out taskId) == true)
            {
                var arguments = call.Arguments.DeepClone().AsObject();
                arguments["taskId"] = taskId;
                return call with { Arguments = arguments };
            }
            return call;
        }

        return new OpenAiFunctionCall(
            call.CallId,
            "ansight_run_task",
            new JsonObject
            {
                ["taskId"] = taskId,
                ["input"] = call.Arguments.DeepClone()
            });
    }

    public bool IsReadOnlyAppToolCall(JsonObject arguments, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var toolId = ReadString(arguments, "toolId");
        lock (appToolCatalogGate)
        {
            return toolId is null
                   || !appToolPermissionsBySession.TryGetValue(sessionId, out var permissions)
                   || !permissions.TryGetValue(toolId, out var permission)
                   || !string.Equals(permission.Policy, "write", StringComparison.OrdinalIgnoreCase);
        }
    }

    public Task<ToolSessionContext?> GetSessionContextAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operations.TryGetSessionSnapshot(sessionId, out var snapshot) || snapshot is null)
        {
            return Task.FromResult<ToolSessionContext?>(null);
        }

        return Task.FromResult<ToolSessionContext?>(CreateSessionContext(snapshot));
    }

    public async Task<SessionCapabilities> GetSessionCapabilitiesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!operations.TryGetSessionSnapshot(sessionId.Trim(), out var snapshot)
            || snapshot is null)
        {
            return SessionCapabilities.Empty();
        }

        var platform = ResolveRuntimePlatform(snapshot);
        if (snapshot.CaptureSource == WorkspaceExecutionModes.Device)
            return SessionCapabilities.Empty(platform) with { IsDeviceOnly = true };
        JsonObject? catalog;
        try
        {
            catalog = await operations.GetSessionAppToolCatalogAsync(
                    sessionId.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            catalog = null;
        }
        return SessionCapabilities.FromPublishedTools(platform, catalog);
    }

    public async Task<ToolSessionContext?> WaitForConnectedSessionAsync(
        string currentSessionId,
        string appId,
        string? targetDeviceIdentifier,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        var normalizedSessionId = currentSessionId.Trim();
        var normalizedAppId = appId.Trim();
        var normalizedDeviceIdentifier = string.IsNullOrWhiteSpace(targetDeviceIdentifier)
            ? null
            : targetDeviceIdentifier.Trim();
        var earliestCreatedUtc = operations.TryGetSessionSnapshot(normalizedSessionId, out var currentSnapshot)
                                 && currentSnapshot is not null
            ? currentSnapshot.CreatedUtc
            : DateTimeOffset.MinValue;
        var deadlineUtc = DateTimeOffset.UtcNow.Add(timeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = operations.GetSessionSummaries()
                .Where(snapshot => string.Equals(snapshot.AppId, normalizedAppId, StringComparison.Ordinal))
                .Where(snapshot => snapshot.CreatedUtc >= earliestCreatedUtc)
                .Where(snapshot => operations.IsSessionConnected(snapshot.SessionId))
                .Where(snapshot => MatchesDevice(snapshot, normalizedDeviceIdentifier))
                .OrderByDescending(snapshot => string.Equals(
                    snapshot.SessionId,
                    normalizedSessionId,
                    StringComparison.Ordinal))
                .ThenByDescending(static snapshot => snapshot.LastUpdatedUtc)
                .FirstOrDefault();
            if (candidate is not null)
            {
                return CreateSessionContext(candidate);
            }

            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ToolSessionContext CreateSessionContext(AppSessionSnapshot snapshot)
    {
        var device = snapshot.DeviceProfile?.Device;
        return new ToolSessionContext(
            snapshot.SessionId,
            snapshot.AppId,
            snapshot.DeviceProfile?.App?.AppName
            ?? (string.IsNullOrWhiteSpace(snapshot.ClientName) ? snapshot.AppId : snapshot.ClientName),
            operations.IsSessionConnected(snapshot.SessionId),
            snapshot.AppState.ToString().ToLowerInvariant(),
            device is null && string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson)
                ? null
                : new SimulatorAgentRunDevice(
                    DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot),
                    device?.Manufacturer,
                    device?.Model,
                    device?.FormFactor,
                    device?.OsName,
                    device?.OsVersion,
                    device?.IsVirtual ?? device?.IsEmulator,
                    device?.IsEmulator));
    }

    private static string ResolveRuntimePlatform(AppSessionSnapshot snapshot)
    {
        var devicePlatform = VisualTreeContract.NormalizeRuntimePlatform(
            snapshot.DeviceProfile?.Device?.OsName);
        if (devicePlatform != VisualTreeContract.UnknownRuntimePlatform)
        {
            return devicePlatform;
        }

        return snapshot.VisualTreeSnapshots
                   .OrderByDescending(static tree => tree.CapturedAtUtc)
                   .Select(static tree => VisualTreeContract.NormalizeRuntimePlatform(tree.RuntimePlatform))
                   .FirstOrDefault(static platform => platform != VisualTreeContract.UnknownRuntimePlatform)
               ?? VisualTreeContract.UnknownRuntimePlatform;
    }

    private static bool MatchesDevice(
        AppSessionSnapshot snapshot,
        string? targetDeviceIdentifier)
        => targetDeviceIdentifier is null
           || string.Equals(
               DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot),
               targetDeviceIdentifier,
               StringComparison.OrdinalIgnoreCase);

    public async Task<ToolCallResult> ExecuteAsync(
        string toolName,
        JsonObject arguments,
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken,
        OperationExecutionContext? context = null)
    {
        if (operations.TryGetSessionSnapshot(sessionId, out var target)
            && target?.CaptureSource == WorkspaceExecutionModes.Device
            && toolName != TypeSecretToolName && !DeviceExecutionTools.IsSupported(toolName))
            return ErrorResult($"Tool '{toolName}' is unavailable in device mode. Query execution capabilities for supported operations.");
        if (string.Equals(toolName, TypeSecretToolName, StringComparison.Ordinal))
        {
            return await TypeSecretAsync(
                arguments,
                sessionId,
                correlationId,
                cancellationToken);
        }

        if (!AllowedToolNames.Contains(toolName))
        {
            var deniedMessage = $"Tool '{toolName}' is not allowed by the simulator agent.";
            return new ToolCallResult(true, deniedMessage, deniedMessage);
        }

        if (string.Equals(toolName, "ansight_call_app_tool", StringComparison.Ordinal)
            && !TryAuthorizeAppToolCall(arguments, sessionId, out var authorizationError))
        {
            return ErrorResult(authorizationError);
        }
        if (string.Equals(toolName, "ansight_list_app_tools", StringComparison.Ordinal)
            && ReadString(arguments, "query") is null
            && ReadString(arguments, "feature") is null
            && ReadString(arguments, "toolId") is null)
        {
            return ErrorResult(
                "Provide a focused query, feature, or exact toolId when discovering app tools; broad catalog dumps are not available to the simulator agent.");
        }
        if (string.Equals(toolName, "ansight_list_tasks", StringComparison.Ordinal)
            && ReadString(arguments, "query") is null
            && ReadString(arguments, "feature") is null)
        {
            return ErrorResult(
                "Provide a focused query or feature when discovering repository tasks; broad task catalog dumps are not available to the simulator agent.");
        }

        var discoveryCacheKey = string.Equals(toolName, "ansight_list_app_tools", StringComparison.Ordinal)
            ? BuildDiscoveryCacheKey(sessionId, arguments)
            : null;
        if (discoveryCacheKey is not null)
        {
            lock (appToolCatalogGate)
            {
                if (appToolDiscoveryCache.TryGetValue(discoveryCacheKey, out var cachedResult))
                {
                    return cachedResult with
                    {
                        Message = $"{cachedResult.Message} Reused cached discovery results for this run."
                    };
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var safeArguments = arguments.DeepClone().AsObject();
        safeArguments["sessionId"] = sessionId;
        safeArguments.Remove("appId");
        safeArguments.Remove("deviceId");
        safeArguments.Remove("bundleIdentifier");
        var uiContext = currentRun.Value?.UiContext;
        var isUiTool = IsUiObservationTool(toolName) || ActionToolNames.Contains(toolName);
        if (isUiTool && uiContext is not null
            && !uiContext.TryResolveSelectors(safeArguments, out var selectorError))
        {
            return ErrorResult(selectorError!);
        }
        ApplySafetyLimits(toolName, safeArguments);

        // Invalidate before dispatch, including failures/cancellation: a task or gesture
        // may have changed the UI before reporting a failure.
        if (isUiTool || toolName is "ansight_run_task" or "ansight_call_app_tool"
            or "ansight_launch_app" or "ansight_terminate_app" or "ansight_start_device")
        {
            uiContext?.Invalidate();
        }

        var result = await operations.CallToolAsync(toolName, safeArguments, correlationId, context);
        // A task may have completed after cancellation was requested. Preserve its
        // retained call evidence before the agent loop observes that cancellation.
        if (!string.Equals(toolName, "ansight_run_task", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (result.IsError)
        {
            var protocolErrorMessage = result.ErrorMessage ?? "The Ansight tool call failed.";
            return new ToolCallResult(
                true,
                new JsonObject
                {
                    ["isError"] = true,
                    ["message"] = protocolErrorMessage
                }.ToJsonString(),
                protocolErrorMessage);
        }

        var payload = result.Payload;
        var isError = payload?["isError"] is JsonValue errorValue
                      && errorValue.TryGetValue<bool>(out var parsedError)
                      && parsedError;
        var structuredContent = payload?["structuredContent"]?.DeepClone()
            ?? payload?.DeepClone()
            ?? new JsonObject();
        JsonObject? accessibilityEvidence = null;
        if (string.Equals(toolName, "ansight_get_live_visual_tree", StringComparison.Ordinal)
            && structuredContent is JsonObject visualTreeContent)
        {
            accessibilityEvidence = CreateAccessibilityTraceEvidence(visualTreeContent);
            structuredContent = VisualTreeObservation.Build(visualTreeContent)
                                ?? structuredContent;
        }
        else if (string.Equals(toolName, "ansight_get_live_navigation_structure", StringComparison.Ordinal)
                 && structuredContent is JsonObject navigationContent)
        {
            structuredContent = NavigationStructureObservation.Build(navigationContent)
                                ?? structuredContent;
        }
        else if (string.Equals(toolName, "ansight_list_app_tools", StringComparison.Ordinal)
                 && structuredContent is JsonObject appToolContent)
        {
            structuredContent = BuildAgentAppToolCatalog(appToolContent, sessionId);
        }
        else if (string.Equals(toolName, "ansight_call_app_tool", StringComparison.Ordinal)
                 && structuredContent is JsonObject appToolResultContent)
        {
            structuredContent = AppToolObservation.Build(appToolResultContent);
        }
        else if (ActionToolNames.Contains(toolName)
                 && structuredContent is JsonObject actionContent)
        {
            accessibilityEvidence = AttachAfterActionObservation(actionContent, sessionId);
        }

        RepositoryTaskSourceTrace? taskSource = null;
        if (toolName == "ansight_run_task" && structuredContent is JsonObject taskResultContent
            && taskResultContent.Remove("sourceTrace", out var sourceNode) && sourceNode is not null)
        {
            taskSource = sourceNode.Deserialize<RepositoryTaskSourceTrace>(JsonUtil.Compact);
        }
        var traceEvidence = LiveUiOcrTraceEvidence.RemoveFrom(structuredContent);

        var resultMessage = ReadResultMessage(structuredContent)
                            ?? (isError ? "The Ansight tool call failed." : "Ansight tool call completed.");
        var output = new JsonObject
        {
            ["isError"] = isError,
            ["result"] = structuredContent
        }.ToJsonString();
        var modelOutput = string.Equals(toolName, "ansight_run_task", StringComparison.Ordinal)
                          && structuredContent is JsonObject taskContent
            ? BuildRepositoryTaskModelOutput(taskContent, isError)
            : null;
        if (isUiTool && structuredContent is JsonObject uiContent)
        {
            var observation = UiResultProjection.Project(uiContent, UiProjectionOptions.Model);
            modelOutput = new JsonObject
            {
                ["isError"] = isError,
                ["result"] = uiContext?.Project(observation) ?? observation
            }.ToJsonString();
            if (modelOutput.Length > MaximumToolOutputCharacters)
            {
                // Never expose half a hierarchy or aliases whose targets were omitted.
                uiContext?.Invalidate();
                modelOutput = new JsonObject
                {
                    ["isError"] = isError,
                    ["truncated"] = true,
                    ["originalCharacterCount"] = modelOutput.Length,
                    ["message"] = "UI observation exceeds the context limit. Narrow the selector or subtree."
                }.ToJsonString();
            }
        }
        ToolCallResult toolResult;
        if (modelOutput is not null || output.Length <= MaximumToolOutputCharacters)
        {
            toolResult = new ToolCallResult(isError, output, resultMessage)
            {
                ModelOutput = modelOutput,
                TaskSource = taskSource,
                TaskAssertions = string.Equals(toolName, "ansight_run_task", StringComparison.Ordinal)
                                 && structuredContent["assertions"] is JsonArray taskAssertions
                    ? taskAssertions.Deserialize<RepositoryTaskAssertion[]>(JsonUtil.Compact)
                    : null,
                TaskCalls = string.Equals(toolName, "ansight_run_task", StringComparison.Ordinal)
                            && structuredContent["toolCalls"] is JsonArray taskCalls
                    ? taskCalls.Deserialize<RepositoryTaskToolCall[]>(JsonUtil.Compact)
                    : null,
                TraceEvidence = traceEvidence,
                AccessibilityEvidence = accessibilityEvidence
            };
        }
        else
        {
            toolResult = new ToolCallResult(
                isError,
                new JsonObject
                {
                    ["isError"] = isError,
                    ["truncated"] = true,
                    ["message"] = "Ansight tool output exceeded the local agent context limit. Narrow the selector or query.",
                    ["originalCharacterCount"] = output.Length,
                    ["prefix"] = output[..Math.Min(output.Length, MaximumTruncatedToolPrefixCharacters)]
                }.ToJsonString(),
                "Ansight tool output exceeded the local agent context limit. Narrow the selector or query.")
            {
                TraceEvidence = traceEvidence,
                AccessibilityEvidence = accessibilityEvidence
            };
        }

        if (discoveryCacheKey is not null)
        {
            lock (appToolCatalogGate)
            {
                appToolDiscoveryCache[discoveryCacheKey] = toolResult;
            }
        }

        return toolResult;
    }

    private static string BuildRepositoryTaskModelOutput(JsonObject content, bool isError)
    {
        var result = new JsonObject();
        foreach (var propertyName in new[] { "taskId", "status", "message", "output" })
        {
            if (content[propertyName] is { } value)
            {
                result[propertyName] = value.DeepClone();
            }
        }

        if (content["assertions"] is JsonArray assertions)
        {
            result["assertions"] = new JsonArray(assertions.OfType<JsonObject>().Select(assertion => (JsonNode?)new JsonObject
            {
                ["assertionId"] = assertion["assertionId"]?.DeepClone(),
                ["passed"] = assertion["passed"]?.DeepClone(),
                ["message"] = assertion["message"]?.DeepClone()
            }).ToArray());
        }

        if (isError)
        {
            foreach (var propertyName in new[] { "error", "errorCode", "standardError" })
            {
                if (content[propertyName] is { } value)
                {
                    result[propertyName] = value.DeepClone();
                }
            }
            if (content["toolCalls"] is JsonArray calls)
            {
                var failedCalls = calls.OfType<JsonObject>()
                    .Where(call => ReadBoolean(call, "isError", false))
                    .ToArray();
                // A script can fail between calls or at an assertion after its last successful call.
                var failureContext = failedCalls.Length > 0
                    ? failedCalls
                    : calls.OfType<JsonObject>().TakeLast(1).ToArray();
                if (failureContext.Length > 0)
                {
                    result["failureContext"] = new JsonArray(failureContext
                        .Select(static call => (JsonNode?)new JsonObject
                        {
                            ["sequence"] = call["sequence"]?.DeepClone(),
                            ["toolName"] = call["toolName"]?.DeepClone(),
                            ["isError"] = call["isError"]?.DeepClone(),
                            ["message"] = call["message"]?.DeepClone()
                        }).ToArray());
                }
            }
        }

        return new JsonObject { ["isError"] = isError, ["result"] = result }.ToJsonString();
    }

    private static JsonObject? CreateAccessibilityTraceEvidence(JsonObject structuredContent)
    {
        if (!string.Equals(
                ReadString(structuredContent, "toolId"),
                LiveUiTreeCapture.DeviceAccessibilityToolId,
                StringComparison.Ordinal)
            || structuredContent["payload"]?["result"] is not JsonObject snapshot)
        {
            return null;
        }

        return new JsonObject
        {
            ["source"] = LiveUiTreeCapture.DeviceAccessibilityToolId,
            ["capturedAtUtc"] = snapshot["capturedAtUtc"]?.DeepClone(),
            ["nodeCount"] = snapshot["nodeCount"]?.DeepClone(),
            ["snapshotId"] = structuredContent["persistedVisualTree"]?["snapshotId"]?.DeepClone(),
            ["snapshot"] = snapshot.DeepClone()
        };
    }

    private JsonObject? AttachAfterActionObservation(JsonObject actionContent, string sessionId)
    {
        var snapshotId = actionContent["evidence"]?["after"]?["visualTreeSnapshotId"] is JsonValue value
                         && value.TryGetValue<string>(out var parsedSnapshotId)
            ? parsedSnapshotId
            : null;
        if (string.IsNullOrWhiteSpace(snapshotId)
            || !operations.TryGetSessionSnapshot(sessionId, out var session)
            || session is null)
        {
            return null;
        }

        var snapshot = session.VisualTreeSnapshots.LastOrDefault(candidate => string.Equals(
            candidate.SnapshotId,
            snapshotId,
            StringComparison.Ordinal));
        if (snapshot is null)
        {
            return null;
        }

        var observation = VisualTreeObservation.Build(
            snapshot.Payload,
            session.SessionId,
            session.AppId,
            snapshot.Source);
        if (observation is not null)
        {
            actionContent["afterObservation"] = observation;
        }

        var format = snapshot.Payload["format"]?.GetValue<string>() ?? snapshot.VisualTreeFormat;
        if (!format.Contains("device-accessibility", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new JsonObject
        {
            ["source"] = LiveUiTreeCapture.DeviceAccessibilityToolId,
            ["capturedAtUtc"] = snapshot.CapturedAtUtc,
            ["nodeCount"] = snapshot.NodeCount,
            ["snapshotId"] = snapshot.SnapshotId,
            ["snapshot"] = snapshot.Payload.DeepClone()
        };
    }

    private static RepositoryTaskShortcut? CreateRepositoryTaskShortcut(
        JsonObject task)
    {
        var taskId = ReadString(task, "taskId");
        var title = ReadString(task, "title");
        var description = ReadString(task, "description");
        if (taskId is null
            || title is null
            || description is null
            || task["inputSchema"] is not JsonObject inputSchema)
        {
            return null;
        }

        return new RepositoryTaskShortcut(
            BuildRepositoryTaskToolName(taskId),
            taskId,
            title,
            description,
            ReadString(task, "feature"),
            inputSchema.DeepClone().AsObject(),
            task["match"] is JsonObject match ? ReadDouble(match, "score") : null,
            task["match"] is JsonObject coverageMatch ? ReadDouble(coverageMatch, "coverage") : null)
        {
            Keywords = task["keywords"] is JsonArray keywords
                ? keywords.OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var keyword) ? keyword : null)
                    .OfType<string>()
                    .ToArray()
                : []
        };
    }

    private static string BuildRepositoryTaskToolName(string taskId)
    {
        var slug = new string(taskId
            .Select(static character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '_'
                    ? char.ToLowerInvariant(character)
                    : '_')
            .ToArray())
            .Trim('_');
        if (slug.Length == 0)
        {
            slug = "repository_task";
        }

        // Every normalized ID gets a suffix: punctuation/case can collide even without truncation.
        var suffix = "_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..12];
        var maximumSlugLength = MaximumRepositoryTaskToolNameLength - RepositoryTaskToolPrefix.Length - suffix.Length;
        if (slug.Length > maximumSlugLength)
        {
            slug = slug[..maximumSlugLength];
        }

        return RepositoryTaskToolPrefix + slug + suffix;
    }

    private static JsonObject BuildRepositoryTaskToolDefinition(
        RepositoryTaskShortcut task)
    {
        var parameters = task.InputSchema.DeepClone().AsObject();
        RemoveInapplicableSchemaKeywords(parameters);
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = task.ToolName,
            ["description"] = $"Repository task '{task.TaskId}': {task.Description}",
            ["parameters"] = parameters
        };
    }

    private JsonObject BuildTypeSecretToolDefinition(
        JsonObject? typeTextDefinition,
        SecretAccess secretAccess)
    {
        var parameters = (typeTextDefinition?["inputSchema"]?.DeepClone() as JsonObject)
                         ?? new JsonObject
                         {
                             ["type"] = "object",
                             ["properties"] = new JsonObject(),
                             ["additionalProperties"] = false
                         };
        RemoveHostControlledTargetProperties(parameters);
        var properties = parameters["properties"] as JsonObject ?? new JsonObject();
        parameters["properties"] = properties;
        properties.Remove("value");
        var aliases = new JsonArray();
        foreach (var alias in secretAccess.Secrets.Keys.OrderBy(
                     static alias => alias,
                     StringComparer.OrdinalIgnoreCase))
        {
            aliases.Add(alias);
        }
        properties["secretAlias"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "A host-managed secret alias declared by this test.",
            ["enum"] = aliases
        };

        var required = parameters["required"] as JsonArray ?? [];
        parameters["required"] = required;
        for (var index = required.Count - 1; index >= 0; index--)
        {
            if (string.Equals(required[index]?.GetValue<string>(), "value", StringComparison.Ordinal))
            {
                required.RemoveAt(index);
            }
        }
        if (!required.Any(node => string.Equals(
                node?.GetValue<string>(),
                "secretAlias",
                StringComparison.Ordinal)))
        {
            required.Add("secretAlias");
        }
        ProjectModelParameters("ansight_type_text", parameters);

        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = TypeSecretToolName,
            ["description"] = "Type a declared host-managed secret into an exact live UI target. The value is resolved locally and is never exposed to the model or audit log.",
            ["parameters"] = parameters
        };
    }

    private async Task<ToolCallResult> TypeSecretAsync(
        JsonObject arguments,
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var alias = ReadString(arguments, "secretAlias");
        if (alias is null)
        {
            return ErrorResult("secretAlias is required.");
        }
        var secretAccess = currentRun.Value?.SecretAccess ?? SecretAccess.Empty;
        if (!secretAccess.Secrets.TryGetValue(alias, out var metadata))
        {
            return ErrorResult($"Secret alias '{alias}' was not declared for this test run.");
        }
        var value = secretAccess.Resolve(metadata.Alias);
        if (value is null)
        {
            return ErrorResult(
                $"Secret alias '{metadata.Alias}' is missing from secure storage. Configure it again before replaying the run.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var safeArguments = arguments.DeepClone().AsObject();
        safeArguments.Remove("secretAlias");
        safeArguments["value"] = value;
        safeArguments["sessionId"] = sessionId;
        safeArguments.Remove("appId");
        safeArguments.Remove("deviceId");
        safeArguments.Remove("bundleIdentifier");
        ApplySafetyLimits("ansight_type_text", safeArguments);
        var uiContext = currentRun.Value?.UiContext;
        if (uiContext is not null && !uiContext.TryResolveSelectors(safeArguments, out var selectorError))
        {
            return ErrorResult(selectorError!);
        }
        uiContext?.Invalidate();
        var result = await operations.CallToolAsync("ansight_type_text", safeArguments, correlationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsError)
        {
            return ErrorResult(
                $"The host could not type secret alias '{metadata.Alias}' into the selected field.");
        }

        var isError = result.Payload?["isError"] is JsonValue errorValue
                      && errorValue.TryGetValue<bool>(out var parsedError)
                      && parsedError;
        return isError
            ? ErrorResult($"The host could not type secret alias '{metadata.Alias}' into the selected field.")
            : new ToolCallResult(
                false,
                new JsonObject
                {
                    ["isError"] = false,
                    ["typed"] = true,
                    ["secretAlias"] = metadata.Alias,
                    ["message"] = "The host-managed secret was typed successfully."
                }.ToJsonString(),
                $"Host-managed secret '{metadata.Alias}' was typed successfully.");
    }

    private static void ApplySafetyLimits(string toolName, JsonObject arguments)
    {
        if (string.Equals(toolName, "ansight_get_live_visual_tree", StringComparison.Ordinal))
        {
            arguments["maxNodes"] = Math.Clamp(ReadInteger(arguments, "maxNodes", 250), 1, 400);
            arguments["maxDepth"] = Math.Clamp(ReadInteger(arguments, "maxDepth", 24), 1, 32);
            arguments["includeProperties"] = false;
            arguments["includeBindableProperties"] = false;
            arguments["includeBindingContexts"] = false;
            arguments["includeInactivePages"] = false;
        }

        if (string.Equals(toolName, "ansight_wait_for_ui", StringComparison.Ordinal))
        {
            arguments["timeoutMs"] = Math.Clamp(ReadInteger(arguments, "timeoutMs", 4_000), 100, 8_000);
            arguments["pollIntervalMs"] = Math.Clamp(ReadInteger(arguments, "pollIntervalMs", 300), 100, 2_000);
        }

        if (string.Equals(toolName, "ansight_scan_screen", StringComparison.Ordinal))
        {
            arguments["limit"] = Math.Clamp(ReadInteger(arguments, "limit", 60), 1, 100);
        }

        if (string.Equals(toolName, "ansight_list_app_tools", StringComparison.Ordinal))
        {
            arguments["policy"] = string.Equals(
                ReadString(arguments, "policy"),
                "write",
                StringComparison.OrdinalIgnoreCase)
                ? "write"
                : "read";
            arguments["executableOnly"] = true;
            arguments["maxResults"] = Math.Clamp(ReadInteger(arguments, "maxResults", 12), 1, 20);
        }

        if (string.Equals(toolName, "ansight_list_tasks", StringComparison.Ordinal))
        {
            arguments["maxResults"] = Math.Clamp(ReadInteger(arguments, "maxResults", 8), 1, 12);
        }

        if (string.Equals(toolName, "ansight_describe_module", StringComparison.Ordinal))
        {
            arguments["includeDefinitions"] = false;
        }

        if (string.Equals(toolName, "ansight_swipe_ui", StringComparison.Ordinal)
            || string.Equals(toolName, "ansight_scroll_ui", StringComparison.Ordinal)
            || string.Equals(toolName, "ansight_pinch_ui", StringComparison.Ordinal))
        {
            arguments["durationMs"] = Math.Clamp(ReadInteger(arguments, "durationMs", 250), 50, 1_000);
        }

        if (ActionToolNames.Contains(toolName))
        {
            arguments["includeScreenshot"] = false;
        }
    }

    private static void RemoveHostControlledTargetProperties(JsonObject parameters)
    {
        if (parameters["properties"] is JsonObject properties)
        {
            properties.Remove("sessionId");
            properties.Remove("appId");
            properties.Remove("deviceId");
            properties.Remove("bundleIdentifier");
        }

        if (parameters["required"] is not JsonArray required)
        {
            return;
        }

        for (var index = required.Count - 1; index >= 0; index--)
        {
            var propertyName = required[index]?.GetValue<string>();
            if (string.Equals(propertyName, "sessionId", StringComparison.Ordinal)
                || string.Equals(propertyName, "appId", StringComparison.Ordinal)
                || string.Equals(propertyName, "deviceId", StringComparison.Ordinal)
                || string.Equals(propertyName, "bundleIdentifier", StringComparison.Ordinal))
            {
                required.RemoveAt(index);
            }
        }
    }

    private static void RemoveParameter(JsonObject parameters, string propertyName)
    {
        if (parameters["properties"] is JsonObject properties)
        {
            properties.Remove(propertyName);
        }

        if (parameters["required"] is not JsonArray required)
        {
            return;
        }

        for (var index = required.Count - 1; index >= 0; index--)
        {
            if (required[index] is JsonValue value
                && value.TryGetValue<string>(out var requiredPropertyName)
                && string.Equals(requiredPropertyName, propertyName, StringComparison.Ordinal))
            {
                required.RemoveAt(index);
            }
        }
    }

    private static void ProjectModelParameters(string toolName, JsonObject parameters)
    {
        if (ActionToolNames.Contains(toolName))
        {
            RemoveParameter(parameters, "includeScreenshot");
        }
        if (toolName == "ansight_get_live_visual_tree")
        {
            foreach (var field in new[] { "includeProperties", "includeBindableProperties", "includeBindingContexts", "includeInactivePages" })
            {
                RemoveParameter(parameters, field);
            }
        }
        if (toolName == "ansight_describe_module")
        {
            RemoveParameter(parameters, "includeDefinitions");
        }
        if (toolName == "ansight_list_app_tools")
        {
            RemoveParameter(parameters, "executableOnly");
        }
        if (toolName == "ansight_find_ui")
        {
            RemoveParameter(parameters, "exact");
        }
        if (toolName is "ansight_swipe_ui" or "ansight_scroll_ui")
        {
            RemoveParameter(parameters, "direction");
            RemoveParameter(parameters, "distance");
        }
        if (toolName == "ansight_run_ui_sequence"
            && parameters["properties"]?["actions"]?["items"] is JsonObject actionSchema)
        {
            RemoveParameter(actionSchema, "direction");
            RemoveParameter(actionSchema, "distance");
        }

        RemoveInapplicableSchemaKeywords(parameters);
        if (parameters["properties"] is not JsonObject properties)
        {
            return;
        }
        foreach (var property in properties)
        {
            if (property.Value is JsonObject schema
                && BuildParameterDescription(toolName, property.Key) is { } description)
            {
                schema["description"] = description;
            }
        }
    }

    private static void RemoveInapplicableSchemaKeywords(JsonNode? node)
    {
        if (node is JsonObject schema)
        {
            // ToolSchema emits additionalProperties even for explicitly non-object types.
            // Keep it for objects and untyped schemas, where it can constrain valid input.
            if (schema["type"] is { } type && !AllowsObjectType(type))
            {
                schema.Remove("additionalProperties");
            }
            foreach (var mapName in new[] { "properties", "$defs", "definitions", "patternProperties" })
            {
                if (schema[mapName] is JsonObject map)
                {
                    foreach (var child in map)
                    {
                        RemoveInapplicableSchemaKeywords(child.Value);
                    }
                }
            }
            foreach (var propertyName in new[] { "items", "additionalProperties", "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "prefixItems" })
            {
                RemoveInapplicableSchemaKeywords(schema[propertyName]);
            }
        }
        else if (node is JsonArray values)
        {
            foreach (var value in values)
            {
                RemoveInapplicableSchemaKeywords(value);
            }
        }
    }

    private static bool AllowsObjectType(JsonNode type)
        => type is JsonValue value && value.TryGetValue<string>(out var name)
            ? name == "object"
            : type is JsonArray types && types.Any(static item =>
                item is JsonValue value && value.TryGetValue<string>(out var name) && name == "object");

    private static string? BuildParameterDescription(string toolName, string parameterName)
        => parameterName switch
        {
            "nodeId" => "Exact node ID from the current live observation; expires with layout changes.",
            "automationId" => "Observed stable automation/accessibility/test ID.",
            "text" => "Text on this target node; all selector fields match the same node.",
            "role" => "Observed semantic role; do not infer it from the user's wording.",
            "type" => "Observed type of the target itself, not its page/container.",
            "ancestorAutomationId" => "Scope to a container with this observed stable ID.",
            "action" => "Action advertised by the target node.",
            "visible" => "Semantic visibility filter; true does not imply on-screen. Check viewportRelation.",
            "enabled" => "Enabled-state filter.",
            "matchMode" when toolName == "ansight_find_ui" => "contains (default), exact, or fuzzy discovery. Act using a returned exact tapHint.",
            "exact" => "Exact string matching; default true.",
            "caseSensitive" => "Case-sensitive matching; default false.",
            "index" => "Zero-based match index after selector filters; default 0.",
            "orientation" when toolName == "ansight_scroll_ui" => "Content to reveal: down/S reveals below, up/N reveals above (opposite finger travel). Compass directions/paths accepted; default up.",
            "orientation" when toolName == "ansight_swipe_ui" => "Finger travel: up/N, down/S, E, W, diagonals or paths such as S to N. Default up.",
            "length" when toolName is "ansight_swipe_ui" or "ansight_scroll_ui" => "Viewport fraction 0.05–0.9; default 0.4.",
            "durationMs" when toolName is "ansight_swipe_ui" or "ansight_scroll_ui" or "ansight_pinch_ui" => "Gesture duration 50–1000 ms; default 250.",
            "startNormalizedX" or "startNormalizedY" or "endNormalizedX" or "endNormalizedY" => "Recorded viewport coordinate 0–1. Supply all four path coordinates together, or omit all.",
            "normalizedX" or "normalizedY" => "Recorded viewport coordinate 0–1. Supply both X/Y; no selector required.",
            "screenX" or "screenY" => "Observed absolute viewport coordinate; supply both X/Y within target and viewport.",
            "targetX" or "targetY" => "Observed target-local screenPosition coordinate; supply both X/Y with its exact surface selector.",
            "replaceExisting" => "Replace existing text (default true); false appends.",
            "timeoutMs" when toolName == "ansight_wait_for_ui" => "Wait limit 100–8000 ms; default 4000.",
            "pollIntervalMs" when toolName == "ansight_wait_for_ui" => "Polling interval 100–2000 ms; default 300.",
            _ => null
        };

    private static void ConfigureVisualTreeParameters(
        JsonObject parameters,
        SessionCapabilities capabilities)
    {
        RemoveParameter(parameters, "arguments");
        if (parameters["properties"] is not JsonObject properties)
        {
            return;
        }

        if (capabilities.VisualTreeToolIds.Count == 0)
        {
            RemoveParameter(parameters, "toolId");
        }
        else if (properties["toolId"] is JsonObject toolIdSchema)
        {
            toolIdSchema["description"] =
                $"Optional visual-tree provider published for the {capabilities.ProfileDescription} app profile.";
            toolIdSchema["enum"] = new JsonArray(
                capabilities.VisualTreeToolIds.Select(static toolId => (JsonNode?)toolId).ToArray());
        }

        if (!capabilities.VisualTreeToolIds.Contains(VisualTreeContract.MauiToolId, StringComparer.Ordinal))
        {
            RemoveParameter(parameters, "root");
            RemoveParameter(parameters, "includeProperties");
            RemoveParameter(parameters, "includeBindableProperties");
            RemoveParameter(parameters, "includeBindingContexts");
            RemoveParameter(parameters, "includeInactivePages");
        }
        if (!capabilities.VisualTreeToolIds.Contains(VisualTreeContract.NativeToolId, StringComparer.Ordinal))
        {
            RemoveParameter(parameters, "includeComputedStyles");
        }
        if (!capabilities.VisualTreeToolIds.Contains(VisualTreeContract.ReactShadowToolId, StringComparer.Ordinal)
            && !capabilities.VisualTreeToolIds.Contains(VisualTreeContract.ReactComponentToolId, StringComparer.Ordinal))
        {
            RemoveParameter(parameters, "includeProps");
        }
        if (!capabilities.VisualTreeToolIds.Contains(VisualTreeContract.ReactComponentToolId, StringComparer.Ordinal))
        {
            RemoveParameter(parameters, "includeState");
        }
    }

    private static void ConfigureNavigationParameters(
        JsonObject parameters,
        SessionCapabilities capabilities)
    {
        if (parameters["properties"]?["framework"] is not JsonObject frameworkSchema)
        {
            return;
        }

        frameworkSchema["description"] =
            $"Optional navigation controller published for the {capabilities.ProfileDescription} app profile.";
        frameworkSchema["enum"] = new JsonArray(
            capabilities.NavigationFrameworks.Select(static framework => (JsonNode?)framework).ToArray());
    }

    private static string BuildToolDescription(
        string toolName,
        string fallback,
        SessionCapabilities capabilities,
        bool appGraphEnabled)
        => toolName switch
        {
            "ansight_get_live_visual_tree" => BuildVisualTreeDescription(capabilities, appGraphEnabled),
            "ansight_get_live_navigation_structure" => BuildNavigationDescription(capabilities, appGraphEnabled),
            "ansight_find_ui" =>
                "Find focused live candidates. Fields are AND constraints on one node. Do not add role/type/action unless observed; scope containers with ancestorAutomationId. matchMode=contains by default; fuzzy is discovery only (matchScore/matchReason): act through an exact returned tapHint. Check resolution and viewportRelation: notRepresented does not prove absence; offscreen needs scrolling. Reuse existing observations for broad inventory. Accessibility first, app/native fallback, then screenshot OCR for text when permitted.",
            "ansight_scan_screen" => appGraphEnabled
                ? "Inventory visible screenshot text missing from semantics. Current-layout tapHints expire after layout changes and cannot supply stable App Graph IDs."
                : "Inventory visible screenshot text missing from semantics. Current-layout tapHints expire after layout changes.",
            "ansight_wait_for_ui" =>
                "Wait for a stable UI condition. Selectors AND-match one node: query a page ID/type separately from descendant text. Accessibility/app/native fallback, then screenshot OCR for text when permitted.",
            "ansight_assert_ui" => "Assert a live semantic UI condition using a focused observed selector.",
            "ansight_tap_ui" =>
                "Tap an exact live target. screenX/Y are viewport coordinates; targetX/Y are observed surface-local screenPosition coordinates with that surface selector. Points must remain within target and viewport.",
            "ansight_type_text" => "Focus an exact live field and type text. Replaces existing text by default.",
            "ansight_swipe_ui" => "Swipe along a recorded path or finger orientation. Up reveals content below. Returns before/after semantic evidence.",
            "ansight_scroll_ui" => "Scroll toward content to reveal: down reveals below, up reveals above. Target the observed scrollable node. Returns before/after semantic evidence.",
            "ansight_pinch_ui" => "Pinch: scale <1 zooms out; >1 zooms in. Returns before/after semantic evidence.",
            "ansight_back_ui" => "Navigate back once and return before/after semantic evidence.",
            "ansight_run_ui_sequence" => "Run 1–32 known recorded gestures when intermediate inspection is unnecessary. Returns endpoint evidence and per-step outcomes.",
            "ansight_list_app_tools" => "Search executable app tools by focused query, feature, or exact toolId. Read policy by default; write only for requested actions. Critical tools excluded. Follow prerequisiteToolIds.",
            "ansight_take_screenshot" => "Save a screenshot artifact. Returns metadata, not pixels; cannot locate targets or resolve semantic gaps.",
            "ansight_call_app_tool" => "Call an exact discovered read/write app tool. Follow prerequisiteToolIds; critical tools are rejected.",
            "ansight_list_tasks" => "Search additional repository tasks with a focused behavioral query/feature when preloaded tasks leave a gap.",
            "ansight_describe_module" => "Describe an exact task/trigger and its schemas when discovery metadata is insufficient.",
            "ansight_run_task" => "Run an exact discovered repository task with its declared input. Enforces session, guards and limits; returns deterministic assertions. Establish its stated prerequisites first.",
            "ansight_list_host_devices" => "List host devices for lifecycle recovery of the selected app.",
            "ansight_start_device" => "Start the host-enforced target device.",
            "ansight_launch_app" => "Launch the host-enforced app on its selected device.",
            "ansight_terminate_app" => "Terminate the host-enforced app on its selected device.",
            _ => fallback
        };

    private static string BuildVisualTreeDescription(
        SessionCapabilities capabilities,
        bool appGraphEnabled)
    {
        var description = "Observe the screen: accessibility-first digest, bounds in the source coordinate space, at most 40 visible nodes. Use focused find for follow-ups.";
        if (capabilities.VisualTreeToolIds.Count > 0)
        {
            description += $" {capabilities.ProfileDescription}; use an enumerated toolId for sparse accessibility.";
        }
        return appGraphEnabled ? description + " Includes App Graph controller evidence." : description;
    }

    private static string BuildNavigationDescription(
        SessionCapabilities capabilities,
        bool appGraphEnabled)
    {
        var description = $"Read navigation state ({string.Join(", ", capabilities.NavigationFrameworks)}). Repeat only after hierarchy changes.";
        return appGraphEnabled ? description + " Includes App Graph guidance." : description;
    }

    private JsonObject BuildAgentAppToolCatalog(JsonObject content, string sessionId)
    {
        var permissions = new Dictionary<string, AppToolPermission>(StringComparer.Ordinal);
        var tools = new JsonArray();
        if (content["catalog"]?["tools"] is JsonArray catalogTools)
        {
            foreach (var tool in catalogTools.OfType<JsonObject>())
            {
                var toolId = ReadString(tool, "id") ?? ReadString(tool, "name");
                if (toolId is null)
                {
                    continue;
                }

                if (string.Equals(toolId, RemoteAppToolIds.UiGetScreenshot, StringComparison.Ordinal))
                {
                    continue;
                }

                var policy = ReadString(tool, "policy") ?? string.Empty;
                var executable = ReadBoolean(tool, "executable", fallback: true)
                                 && tool["denial"] is not JsonObject;
                var denialReason = tool["denial"] is JsonObject denial
                    ? ReadString(denial, "reason")
                    : null;
                var permission = new AppToolPermission(
                    toolId,
                    policy,
                    executable,
                    denialReason);
                permissions[toolId] = permission;
                if (!permission.IsAllowedForAutomation)
                {
                    continue;
                }

                tools.Add(new JsonObject
                {
                    ["toolId"] = toolId,
                    ["name"] = ReadString(tool, "name"),
                    ["description"] = ReadString(tool, "description"),
                    ["category"] = ReadString(tool, "category"),
                    ["policy"] = policy,
                    ["argumentsSchema"] = tool["argumentsSchema"]?.DeepClone(),
                    ["resultSchema"] = tool["resultSchema"]?.DeepClone(),
                    ["prerequisiteToolIds"] = tool["prerequisiteToolIds"]?.DeepClone()
                });
            }
        }

        lock (appToolCatalogGate)
        {
            var merged = appToolPermissionsBySession.TryGetValue(sessionId, out var existing)
                ? new Dictionary<string, AppToolPermission>(existing, StringComparer.Ordinal)
                : new Dictionary<string, AppToolPermission>(StringComparer.Ordinal);
            foreach (var permission in permissions)
            {
                merged[permission.Key] = permission.Value;
            }

            appToolPermissionsBySession[sessionId] = merged;
        }

        return new JsonObject
        {
            ["sessionId"] = content["sessionId"]?.DeepClone(),
            ["appId"] = content["appId"]?.DeepClone(),
            ["message"] = tools.Count == 0
                ? "The selected app exposes no executable app tools permitted by the simulator automation policy for this query."
                : $"The selected app exposes {tools.Count} executable validation or runtime-control tool(s) permitted by simulator automation policy.",
            ["toolCount"] = tools.Count,
            ["tools"] = tools
        };
    }

    private bool TryAuthorizeAppToolCall(
        JsonObject arguments,
        string sessionId,
        out string error)
    {
        var toolId = ReadString(arguments, "toolId");
        if (toolId is null)
        {
            error = "toolId is required.";
            return false;
        }

        AppToolPermission? permission;
        lock (appToolCatalogGate)
        {
            permission = appToolPermissionsBySession.TryGetValue(sessionId, out var permissions)
                         && permissions.TryGetValue(toolId, out var value)
                ? value
                : null;
        }

        if (permission is null)
        {
            error = "Call ansight_list_app_tools first, then use an exact toolId from that live catalog.";
            return false;
        }

        if (!permission.IsExecutable)
        {
            error = permission.DenialReason
                    ?? $"App tool '{toolId}' is not executable in the current app state.";
            return false;
        }

        if (!permission.IsAllowedForAutomation)
        {
            error = $"App tool '{toolId}' is outside the simulator agent's non-critical validation and runtime-control policy.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static ToolCallResult ErrorResult(string message)
        => new(
            true,
            new JsonObject
            {
                ["isError"] = true,
                ["message"] = message
            }.ToJsonString(),
            message);

    private static string? ReadResultMessage(JsonNode structuredContent)
    {
        if (structuredContent is not JsonObject content)
        {
            return null;
        }

        return ReadString(content, "message")
               ?? (content["result"] is JsonObject result ? ReadString(result, "message") : null)
               ?? (content["payload"] is JsonObject payload
                   ? ReadString(payload, "message")
                     ?? (payload["result"] is JsonObject payloadResult
                         ? ReadString(payloadResult, "message")
                         : null)
                   : null);
    }

    private static int ReadInteger(JsonObject value, string propertyName, int fallback)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<int>(out var number)
            ? number
            : fallback;

    private static bool ReadBoolean(JsonObject value, string propertyName, bool fallback)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<bool>(out var result)
            ? result
            : fallback;

    private static double? ReadDouble(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<double>(out var result)
            ? result
            : null;

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static DiscoveryCacheKey BuildDiscoveryCacheKey(
        string sessionId,
        JsonObject arguments)
        => new(
            sessionId.Trim(),
            ReadString(arguments, "query")?.ToLowerInvariant(),
            ReadString(arguments, "feature")?.ToLowerInvariant(),
            ReadString(arguments, "toolId")?.ToLowerInvariant(),
            string.Equals(ReadString(arguments, "policy"), "write", StringComparison.OrdinalIgnoreCase)
                ? "write"
                : "read");

    private sealed class SimulatorAgentToolRunState(
        SecretAccess secretAccess,
        IDisposable captureScope)
    {
        public SecretAccess SecretAccess { get; } = secretAccess;

        public IDisposable CaptureScope { get; set; } = captureScope;

        public ModelUiContext UiContext { get; } = new();

        public Dictionary<string, string> RepositoryTaskIdsByToolName { get; } =
            new(StringComparer.Ordinal);
    }

    private static bool IsUiObservationTool(string toolName)
        => toolName is "ansight_get_live_visual_tree" or "ansight_find_ui"
            or "ansight_wait_for_ui" or "ansight_assert_ui";

    public async Task<ToolCallResult?> CaptureInitialObservationAsync(
        string sessionId, SessionCapabilities capabilities, string correlationId, CancellationToken cancellationToken)
        => await ExecuteAsync("ansight_get_live_visual_tree", capabilities.CreateInitialObservationArguments(), sessionId, correlationId, cancellationToken).ConfigureAwait(false);
}
