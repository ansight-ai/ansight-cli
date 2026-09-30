namespace Ansight.Host.Workspaces.History;

public sealed partial class WorkspaceTestHistoryService
{
    private readonly RuntimeCoordinator runtime;
    private readonly SimulatorAgentService simulatorAgent;
    private readonly WorkspaceTestBatchAuditStore batchAuditStore;

    internal WorkspaceTestHistoryService(
        RuntimeCoordinator runtime,
        SimulatorAgentService simulatorAgent,
        WorkspaceTestBatchAuditStore batchAuditStore)
    {
        this.runtime = runtime;
        this.simulatorAgent = simulatorAgent;
        this.batchAuditStore = batchAuditStore;
    }

    public WorkspaceTestHistoryResult List(
        string? appId = null,
        string? workspacePath = null,
        int limit = 100,
        string? sessionId = null,
        string? batchRunId = null)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "History limit must be between 1 and 10,000.");
        }

        var normalizedAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var normalizedWorkspacePath = string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : Path.GetFullPath(workspacePath.Trim());
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        var normalizedBatchRunId = string.IsNullOrWhiteSpace(batchRunId) ? null : batchRunId.Trim();
        var batches = batchAuditStore.List()
            .Where(entry => normalizedWorkspacePath is null
                            || AreSamePath(entry.Audit.WorkspacePath, normalizedWorkspacePath))
            .Where(entry => normalizedAppId is null
                            || entry.Audit.Tests.Any(test => string.Equals(
                                test.AppId,
                                normalizedAppId,
                                StringComparison.OrdinalIgnoreCase)))
            .Where(entry => normalizedSessionId is null
                            || entry.Audit.Tests.Any(test => string.Equals(
                                test.SessionId,
                                normalizedSessionId,
                                StringComparison.Ordinal)))
            .Where(entry => normalizedBatchRunId is null
                            || string.Equals(
                                entry.Audit.BatchRunId,
                                normalizedBatchRunId,
                                StringComparison.OrdinalIgnoreCase))
            .Select(entry => normalizedAppId is null
                ? entry
                : FilterBatchHistoryEntry(entry, normalizedAppId))
            .Take(limit)
            .ToArray();
        var workspacePathsByAppId = runtime.Apps.List()
            .Where(static app => !string.IsNullOrWhiteSpace(app.CodebasePath))
            .ToDictionary(
                static app => app.AppId,
                static app => app.CodebasePath!,
                StringComparer.OrdinalIgnoreCase);
        var catalogsByWorkspacePath = new Dictionary<string, WorkspaceTestCatalogResult?>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var runs = simulatorAgent.ListRunHistory()
            .Where(entry => normalizedAppId is null
                            || string.Equals(
                                entry.Audit.AppId,
                                normalizedAppId,
                                StringComparison.OrdinalIgnoreCase))
            .Where(entry => normalizedWorkspacePath is null
                            || entry.Audit.WorkspacePath is { } runWorkspacePath
                            && AreSamePath(runWorkspacePath, normalizedWorkspacePath))
            .Where(entry => normalizedSessionId is null
                            || string.Equals(
                                entry.Audit.SessionId,
                                normalizedSessionId,
                                StringComparison.Ordinal))
            .Where(entry => normalizedBatchRunId is null
                            || string.Equals(
                                entry.Audit.BatchRunId,
                                normalizedBatchRunId,
                                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static entry => entry.Audit.StartedUtc)
            .Take(limit)
            .Select(entry => CreateRunHistorySummary(
                entry,
                ResolveWorkspaceTestIdentity(
                    entry.Audit,
                    workspacePathsByAppId,
                    catalogsByWorkspacePath)))
            .ToArray();
        return new WorkspaceTestHistoryResult(
            batchAuditStore.HistoryDirectoryPath,
            batches,
            runs);
    }

    public WorkspaceTestHistoryInspection? Inspect(
        string runId,
        string? appId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var normalizedRunId = runId.Trim();
        var normalizedAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var batches = batchAuditStore.List();
        var batch = batches.FirstOrDefault(entry => string.Equals(
            entry.Audit.BatchRunId,
            normalizedRunId,
            StringComparison.OrdinalIgnoreCase));
        var runHistory = simulatorAgent.ListRunHistory();
        if (batch is not null)
        {
            if (normalizedAppId is not null
                && !batch.Audit.Tests.Any(test => string.Equals(
                    test.AppId,
                    normalizedAppId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var filteredBatch = normalizedAppId is null
                ? batch
                : FilterBatchHistoryEntry(batch, normalizedAppId);
            var childRunIds = filteredBatch.Audit.Tests
                .Select(static test => test.AgentRunId)
                .Where(static childRunId => !string.IsNullOrWhiteSpace(childRunId))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var childRuns = runHistory
                .Where(entry => (childRunIds.Contains(entry.Audit.RunId)
                                 || string.Equals(
                                     entry.Audit.BatchRunId,
                                     normalizedRunId,
                                     StringComparison.OrdinalIgnoreCase))
                                && (normalizedAppId is null
                                    || string.Equals(
                                        entry.Audit.AppId,
                                        normalizedAppId,
                                        StringComparison.OrdinalIgnoreCase)))
                .OrderBy(static entry => entry.Audit.StartedUtc)
                .ToArray();
            return new WorkspaceTestHistoryInspection(normalizedRunId, filteredBatch, childRuns);
        }

        var correlatedRuns = runHistory
            .Where(entry => string.Equals(
                                entry.Audit.BatchRunId,
                                normalizedRunId,
                                StringComparison.OrdinalIgnoreCase)
                            && (normalizedAppId is null
                                || string.Equals(
                                    entry.Audit.AppId,
                                    normalizedAppId,
                                    StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static entry => entry.Audit.StartedUtc)
            .ToArray();
        if (correlatedRuns.Length > 0)
        {
            return new WorkspaceTestHistoryInspection(normalizedRunId, null, correlatedRuns);
        }

        var run = runHistory.FirstOrDefault(entry =>
            string.Equals(
                entry.Audit.RunId,
                normalizedRunId,
                StringComparison.OrdinalIgnoreCase)
            && (normalizedAppId is null
                || string.Equals(
                    entry.Audit.AppId,
                    normalizedAppId,
                    StringComparison.OrdinalIgnoreCase)));
        if (run is null)
        {
            return null;
        }

        var parentBatch = string.IsNullOrWhiteSpace(run.Audit.BatchRunId)
            ? null
            : batches.FirstOrDefault(entry => string.Equals(
                entry.Audit.BatchRunId,
                run.Audit.BatchRunId,
                StringComparison.OrdinalIgnoreCase));
        if (parentBatch is not null && normalizedAppId is not null)
        {
            parentBatch = FilterBatchHistoryEntry(parentBatch, normalizedAppId);
        }

        return new WorkspaceTestHistoryInspection(normalizedRunId, parentBatch, [run]);
    }

    public string? ResolveTraceEvidencePath(
        string runId,
        string relativePath,
        string? appId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var inspection = Inspect(runId, appId);
        var run = inspection?.Runs.FirstOrDefault(entry => string.Equals(
            entry.Audit.RunId,
            runId.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (run is null)
        {
            return null;
        }

        var normalizedRelativePath = relativePath.Replace('\\', '/').TrimStart('/');
        var allowedPaths = run.Audit.ToolCalls
            .SelectMany(static call => new[]
            {
                call.OcrEvidence?.ScreenshotPath,
                call.OcrEvidence?.ResultsPath
            })
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Replace('\\', '/').TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!allowedPaths.Contains(normalizedRelativePath)
            || Path.IsPathRooted(relativePath)
            || normalizedRelativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            return null;
        }

        var auditDirectoryPath = Path.GetDirectoryName(run.FilePath);
        if (string.IsNullOrWhiteSpace(auditDirectoryPath))
        {
            return null;
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(
            auditDirectoryPath,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToAuditDirectory = Path.GetRelativePath(auditDirectoryPath, resolvedPath);
        return relativeToAuditDirectory.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               || string.Equals(relativeToAuditDirectory, "..", StringComparison.Ordinal)
               || !File.Exists(resolvedPath)
            ? null
            : resolvedPath;
    }

    internal static WorkspaceTestBatchRunHistoryEntry FilterBatchHistoryEntry(
        WorkspaceTestBatchRunHistoryEntry entry,
        string appId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        var tests = entry.Audit.Tests
            .Where(test => string.Equals(
                test.AppId,
                normalizedAppId,
                StringComparison.OrdinalIgnoreCase))
            .Select((test, index) => test with { Index = index + 1 })
            .ToArray();
        var status = ResolveFilteredBatchStatus(entry.Audit.Status, tests);
        var message = status == WorkspaceTestHistoryStatuses.Running
            ? $"Running {tests.Length:N0} workspace test(s) for '{normalizedAppId}'."
            : $"{tests.Length:N0} test(s) for '{normalizedAppId}': "
              + $"{tests.Count(static test => test.Status == WorkspaceTestHistoryStatuses.Succeeded):N0} passed, "
              + $"{tests.Count(static test => test.Status == WorkspaceTestHistoryStatuses.Failed):N0} failed, "
              + $"{tests.Count(static test => test.Status == WorkspaceTestHistoryStatuses.Skipped):N0} skipped.";
        return entry with
        {
            Audit = entry.Audit with
            {
                RequestedTestIds = tests.Select(static test => test.TestId).ToArray(),
                Status = status,
                Message = message,
                Tests = tests
            }
        };
    }

    private static string ResolveFilteredBatchStatus(
        string originalStatus,
        IReadOnlyList<WorkspaceTestBatchItemAudit> tests)
    {
        if (tests.Any(static test => test.Status == WorkspaceTestHistoryStatuses.Failed))
        {
            return WorkspaceTestHistoryStatuses.Failed;
        }

        if (tests.Any(static test => test.Status == WorkspaceTestHistoryStatuses.Cancelled))
        {
            return WorkspaceTestHistoryStatuses.Cancelled;
        }

        if (tests.Any(static test => test.Status == WorkspaceTestHistoryStatuses.Pending)
            || string.Equals(originalStatus, WorkspaceTestHistoryStatuses.Running, StringComparison.Ordinal))
        {
            return WorkspaceTestHistoryStatuses.Running;
        }

        if (tests.Any(static test => test.Status == WorkspaceTestHistoryStatuses.Skipped))
        {
            return WorkspaceTestHistoryStatuses.Skipped;
        }

        return tests.Count > 0
            ? WorkspaceTestHistoryStatuses.Succeeded
            : originalStatus;
    }

}

public sealed partial class WorkspaceTestHistoryService
{
    private WorkspaceTestIdentity ResolveWorkspaceTestIdentity(
        SimulatorAgentRunAudit audit,
        IReadOnlyDictionary<string, string> workspacePathsByAppId,
        IDictionary<string, WorkspaceTestCatalogResult?> catalogsByWorkspacePath)
    {
        var workspacePath = audit.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath)
            && !string.IsNullOrWhiteSpace(audit.AppId))
        {
            workspacePathsByAppId.TryGetValue(audit.AppId, out workspacePath);
        }

        if (!string.IsNullOrWhiteSpace(audit.WorkspaceTestId)
            && !string.IsNullOrWhiteSpace(audit.WorkspaceTestName))
        {
            return new WorkspaceTestIdentity(
                workspacePath,
                audit.WorkspaceTestId,
                audit.WorkspaceTestName);
        }

        if (string.IsNullOrWhiteSpace(workspacePath)
            || !Directory.Exists(workspacePath))
        {
            return new WorkspaceTestIdentity(
                workspacePath,
                audit.WorkspaceTestId,
                audit.WorkspaceTestName);
        }

        if (!catalogsByWorkspacePath.TryGetValue(workspacePath, out var catalog))
        {
            try
            {
                catalog = WorkspaceTestCatalog.Load(workspacePath);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                catalog = null;
            }

            catalogsByWorkspacePath[workspacePath] = catalog;
        }

        var matchedTest = MatchWorkspaceTest(
            catalog?.Tests ?? [],
            audit.AppId,
            audit.RequestedInstructions.FirstOrDefault());
        return new WorkspaceTestIdentity(
            workspacePath,
            audit.WorkspaceTestId ?? matchedTest?.TestId,
            audit.WorkspaceTestName ?? matchedTest?.Name);
    }

    internal static WorkspaceTestDefinition? MatchWorkspaceTest(
        IReadOnlyList<WorkspaceTestDefinition> tests,
        string? appId,
        string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return null;
        }

        var candidates = tests
            .Where(test => string.IsNullOrWhiteSpace(appId)
                           || string.Equals(test.AppId, appId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var exactMatches = candidates
            .Where(test => string.Equals(
                test.BuildRunnerPrompt(),
                instruction,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exactMatches.Length == 1)
        {
            return exactMatches[0];
        }

        var scenarioMatches = candidates
            .Where(test => instruction.Contains(
                $"Test scenario:{Environment.NewLine}{test.Prompt.Trim()}",
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return scenarioMatches.Length == 1 ? scenarioMatches[0] : null;
    }

    internal static string BuildRunDisplayName(
        string? testName,
        string? testId,
        string? instruction)
    {
        if (!string.IsNullOrWhiteSpace(testName))
        {
            return testName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(testId))
        {
            return testId.Trim();
        }

        var title = instruction?.Trim() ?? string.Empty;
        const string scenarioPrefix = "Test scenario:";
        if (title.StartsWith(scenarioPrefix, StringComparison.OrdinalIgnoreCase))
        {
            title = title[scenarioPrefix.Length..].TrimStart();
        }

        title = title
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?? string.Empty;
        if (title.Length <= 72)
        {
            return string.IsNullOrWhiteSpace(title) ? "Untitled test" : title;
        }

        var shortened = title[..72];
        var lastSpace = shortened.LastIndexOf(' ');
        if (lastSpace >= 40)
        {
            shortened = shortened[..lastSpace];
        }

        return shortened.TrimEnd(' ', '.', ',', ';', ':') + "…";
    }

    private static WorkspaceTestRunHistorySummary CreateRunHistorySummary(
        SimulatorAgentRunHistoryEntry entry,
        WorkspaceTestIdentity identity)
    {
        var audit = entry.Audit;
        return new WorkspaceTestRunHistorySummary(
            audit.RunId,
            audit.BatchRunId,
            identity.WorkspacePath,
            identity.TestId,
            identity.TestName,
            BuildRunDisplayName(
                identity.TestName,
                identity.TestId,
                audit.RequestedInstructions.FirstOrDefault()),
            audit.AppId,
            audit.SessionId,
            audit.Model,
            audit.Status.ToString().ToLowerInvariant(),
            audit.Message,
            audit.StartedUtc,
            audit.CompletedUtc,
            audit.DurationMilliseconds,
            audit.Tokens.TotalTokens,
            audit.ModelPassCount,
            audit.AnsightToolCallCount,
            entry.FilePath)
        {
            Reasoning = audit.Reasoning,
            CalculatedCost = audit.CalculatedCost
        };
    }

    private sealed record WorkspaceTestIdentity(
        string? WorkspacePath,
        string? TestId,
        string? TestName);

    private static bool AreSamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

}
