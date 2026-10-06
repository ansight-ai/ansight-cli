using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host;
using Ansight.Host.Workspaces;
using Ansight.Host.Workspaces.Cloud;
using Ansight.Host.Workspaces.Execution;
using Ansight.Infrastructure;

namespace Ansight.Cli.Commands.Test;

internal static class TestCommands
{
    internal static readonly JsonSerializerOptions auditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        MaxDepth = 256,
        WriteIndented = true
    };

    public static Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return Task.FromResult(CliCommandHelp.Write(output, BuildHelp()));
        }

        var subcommand = arguments.RequirePositional(1, "test subcommand").ToLowerInvariant();
        return subcommand switch
        {
            "list" => Task.FromResult(List(arguments, output, validate: false)),
            "validate" => Task.FromResult(List(arguments, output, validate: true)),
            "run" => RunTestAsync(arguments, output, cancellationToken),
            "run-inline" => AppExecutionService.RunInlineCompatibilityAsync(
                arguments,
                output,
                cancellationToken),
            "run-all" => RunAllAsync(arguments, output, cancellationToken),
            "history" => HistoryAsync(arguments, output),
            "inspect" => InspectHistoryAsync(arguments, output),
            "export" => ExportHistoryAsync(arguments, output, cancellationToken),
            "retry-metering" => RetryMeteringAsync(arguments, output, cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown test subcommand '{subcommand}'. Expected list, validate, run, run-inline, run-all, history, inspect, export, or retry-metering.")
        };
    }

    internal static int List(CliArguments arguments, CliOutput output, bool validate)
    {
        var workspacePath = arguments.RequirePositional(2, "workspace path");
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var warnings = catalog.Warnings;
        var result = new TestCatalogOutput(
            "ansight.workspace-tests/v1",
            catalog.WorkspacePath,
            catalog.Tests,
            warnings,
            warnings.Count == 0);
        output.Write(result, () => RenderCatalog(result, validate));
        return validate && catalog.Warnings.Count > 0
            ? CliExitCodes.Failure
            : CliExitCodes.Success;
    }

    internal static async Task<int> RunTestAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var workspacePath = arguments.RequirePositional(2, "workspace path");
        var testId = arguments.RequirePositional(3, "test identifier");
        var targets = WorkspaceTestTargetOptions.ResolveMany(arguments);
        if (targets.Count > 1)
        {
            if (arguments.HasFlag("session-id"))
            {
                throw new CliUsageException(
                    "--session-id selects one connected session and cannot be combined with multiple --device-id values.");
            }

            return await RunBatchAsync(
                arguments,
                output,
                Path.GetFullPath(workspacePath),
                [testId],
                targets,
                cancellationToken).ConfigureAwait(false);
        }

        var target = targets.Count == 1 ? targets[0] : null;
        var options = await CliTestRuntimeOptionsResolver.ResolveAsync(
            CliRuntime.ResolveOptions(arguments),
            target,
            cancellationToken).ConfigureAwait(false);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        using var commandContext = CliCommandContext.Current is null
            ? CliCommandContext.Push(lease.Runtime, options.DataDirectory, secretValue: null)
            : null;
        var progress = new CliProgress<WorkspaceTestRunProgress>(value =>
        {
            if (ShouldWriteProgress(value, arguments.IsVerbose))
            {
                output.WriteProgress($"[{value.Stage}] {value.Message}");
            }
        });
        var request = new WorkspaceTestRunRequest(
            workspacePath,
            testId,
            arguments.GetOption("session-id"),
            arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45)),
            ReasoningOptions.ResolveModelOverride(arguments),
            arguments.GetIntOption("max-turns", 64, 1, 512),
            arguments.GetIntOption("max-round-trips", 64, 1, 4_096),
            arguments.GetIntOption("max-tool-calls", 512, 1, 16_384),
            !arguments.HasFlag("stop-on-failure"),
            target,
            WorkspaceTestTeamOptions.Resolve(arguments),
            !arguments.HasFlag("no-workspace-tools"))
        {
            BatchRunId = ResolveBatchRunId(arguments),
            Reasoning = ReasoningOptions.Resolve(arguments),
            CaptureTrace = arguments.HasFlag("trace"),
            OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments),
            SecretResolver = ResolveTestSecret
        };
        var result = await lease.Runtime.WorkspaceTests.RunAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        var exitCode = ResolveRunExitCode(result);
        var resultFilePath = TestResultExporter.CreatePath(
            options.DataDirectory,
            arguments.GetOption("result-file"),
            testId,
            result.AgentResult?.Audit.RunId);
        var response = new TestRunOutput(
            "ansight.workspace-test-run/v3",
            DateTimeOffset.UtcNow,
            Path.GetFullPath(workspacePath),
            testId,
            resultFilePath,
            exitCode,
            result);
        TestResultExporter.Save(resultFilePath, response, pruneGeneratedResults: arguments.GetOption("result-file") is null);
        output.Write(response, () => RenderRun(
            result,
            resultFilePath,
            arguments.HasFlag("audit"),
            testId));
        return exitCode;
    }

    internal static async Task<int> RunAllAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var workspacePath = Path.GetFullPath(arguments.RequirePositional(2, "workspace path"));
        var requestedTestIds = arguments.GetOptions("test");
        var catalog = WorkspaceTestCatalog.Load(workspacePath, cancellationToken);
        var configurationError = GetRunAllConfigurationError(catalog, requestedTestIds);
        if (configurationError is not null)
        {
            output.WriteError("configuration", configurationError, CliExitCodes.Configuration);
            if (!output.IsJson)
            {
                output.WriteText("🔴");
            }

            return CliExitCodes.Configuration;
        }

        var targets = WorkspaceTestTargetOptions.ResolveMany(arguments);
        return await RunBatchAsync(
            arguments,
            output,
            workspacePath,
            requestedTestIds,
            targets,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RunBatchAsync(
        CliArguments arguments,
        CliOutput output,
        string workspacePath,
        IReadOnlyList<string> requestedTestIds,
        IReadOnlyList<WorkspaceTestTargetRequest> targets,
        CancellationToken cancellationToken)
    {
        var target = targets.Count == 1 ? targets[0] : null;
        var options = await CliTestRuntimeOptionsResolver.ResolveAsync(
            CliRuntime.ResolveOptions(arguments),
            targets,
            cancellationToken).ConfigureAwait(false);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        using var commandContext = CliCommandContext.Current is null
            ? CliCommandContext.Push(lease.Runtime, options.DataDirectory, secretValue: null)
            : null;
        var progress = new CliProgress<WorkspaceTestBatchProgress>(value =>
        {
            if (value.TestProgress is null
                || ShouldWriteProgress(value.TestProgress, arguments.IsVerbose))
            {
                var targetLabel = value.TargetCount > 1
                    ? $" [{value.TargetIndex}/{value.TargetCount} {value.DeviceIdentifier ?? "auto"}]"
                    : string.Empty;
                output.WriteProgress(
                    $"[{value.TestIndex}/{value.TestCount}]{targetLabel} {value.TestId}: {value.Message}");
            }
        });
        var request = new WorkspaceTestBatchRequest(
            workspacePath,
            requestedTestIds,
            arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45)),
            ReasoningOptions.ResolveModelOverride(arguments),
            arguments.GetIntOption("max-turns", 64, 1, 512),
            arguments.GetIntOption("max-round-trips", 64, 1, 4_096),
            arguments.GetIntOption("max-tool-calls", 512, 1, 16_384),
            !arguments.HasFlag("stop-on-failure"),
            target,
            WorkspaceTestTeamOptions.Resolve(arguments),
            !arguments.HasFlag("no-workspace-tools"))
        {
            Source = "cli",
            Reasoning = ReasoningOptions.Resolve(arguments),
            CaptureTrace = arguments.HasFlag("trace"),
            OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments),
            SecretResolver = ResolveTestSecret,
            Targets = targets.Count > 1 ? targets : null,
            Parallel = arguments.HasFlag("parallel")
        };
        var result = await lease.Runtime.WorkspaceTests.Batches.RunAllAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        var exitCode = ResolveBatchExitCode(result);
        var resultFilePath = TestResultExporter.CreatePath(
            options.DataDirectory,
            arguments.GetOption("result-file"),
            "batch");
        var response = new TestBatchOutput(
            "ansight.workspace-test-batch/v3",
            DateTimeOffset.UtcNow,
            Path.GetFullPath(workspacePath),
            resultFilePath,
            exitCode,
            result);
        TestResultExporter.Save(resultFilePath, response, pruneGeneratedResults: arguments.GetOption("result-file") is null);
        output.Write(response, () => RenderBatch(result, resultFilePath, arguments.HasFlag("audit")));
        return exitCode;
    }

    internal static string? GetRunAllConfigurationError(
        WorkspaceTestCatalogResult catalog,
        IReadOnlyList<string> requestedTestIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(requestedTestIds);
        var enabledTests = catalog.Tests.Where(static test => test.Enabled).ToArray();
        if (enabledTests.Length == 0)
        {
            var warningDetails = catalog.Warnings.Count == 0
                ? string.Empty
                : $" {string.Join(" ", catalog.Warnings.Select(static warning => $"Warning: {warning}"))}";
            var disabledDetails = catalog.Tests.Count == 0
                ? string.Empty
                : " All discovered tests are disabled.";
            return $"No runnable workspace tests were found beneath '{catalog.WorkspacePath}'.{disabledDetails}{warningDetails}";
        }

        if (requestedTestIds.Count == 0)
        {
            return null;
        }

        var availableIds = catalog.Tests
            .Select(static test => test.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingIds = requestedTestIds
            .Where(id => !availableIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingIds.Length > 0)
        {
            return $"Workspace test(s) not found beneath '{catalog.WorkspacePath}': {string.Join(", ", missingIds)}.";
        }

        var enabledIds = enabledTests
            .Select(static test => test.TestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabledIds = requestedTestIds
            .Where(id => !enabledIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return disabledIds.Length == 0
            ? null
            : $"Workspace test(s) are disabled: {string.Join(", ", disabledIds)}.";
    }

    internal static string? ResolveTestSecret(string alias)
        => CliCommandContext.Current?.ResolveSecret(alias)
           ?? CliCommandContext.ResolveScopedSecret(alias);

    internal static string? ResolveBatchRunId(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.HasFlag("batch"))
        {
            return null;
        }

        var batchRunId = arguments.GetOption("batch")?.Trim();
        if (string.IsNullOrWhiteSpace(batchRunId))
        {
            throw new CliUsageException("--batch requires a run ID.");
        }

        if (batchRunId.Length > 128
            || batchRunId.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new CliUsageException(
                "--batch must contain at most 128 letters, numbers, hyphens, or underscores.");
        }

        return batchRunId;
    }

    internal static int ResolveBatchExitCode(WorkspaceTestBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.WasCancelled)
        {
            return CliExitCodes.Cancelled;
        }

        if (result.Results.Count == 0)
        {
            return CliExitCodes.Configuration;
        }

        return result.FailedCount == 0
            ? CliExitCodes.Success
            : CliExitCodes.TestFailed;
    }

    internal static async Task<int> HistoryAsync(CliArguments arguments, CliOutput output)
    {
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            CancellationToken.None).ConfigureAwait(false);
        var appId = arguments.GetOption("app-id");
        var workspacePath = arguments.GetOption("workspace");
        var history = lease.Runtime.WorkspaceTests.History.List(
            appId,
            workspacePath,
            arguments.GetIntOption("limit", 100, 1, 10_000),
            batchRunId: ResolveBatchRunId(arguments));
        output.Write(
            new TestHistoryOutput(
                "ansight.workspace-test-history/v2",
                appId,
                workspacePath,
                history),
            () => history.Batches.Count == 0 && history.Runs.Count == 0
                ? "No test-run audits found."
                : RenderHistory(history));
        return CliExitCodes.Success;
    }

    internal static async Task<int> InspectHistoryAsync(CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight test inspect <run-id>");
        var runId = arguments.RequirePositional(2, "test or batch run identifier");
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            CancellationToken.None).ConfigureAwait(false);
        var inspection = lease.Runtime.WorkspaceTests.History.Inspect(runId);
        if (inspection is null)
        {
            output.WriteError(
                "test_run_not_found",
                $"Test or batch run '{runId}' was not found in local history.",
                CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        output.Write(
            new TestHistoryInspectionOutput(
                "ansight.workspace-test-history-inspection/v1",
                inspection),
            () => RenderHistoryInspection(inspection));
        return CliExitCodes.Success;
    }

    internal static async Task<int> ExportHistoryAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(4, "ansight test export <run-id> <output.zip>");
        var runId = arguments.RequirePositional(2, "test or batch run identifier");
        var outputPath = Path.GetFullPath(arguments.RequirePositional(3, "evaluation bundle output path"));
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var result = await lease.Runtime.WorkspaceTests.TraceExports.ExportAsync(
                runId,
                outputPath,
                arguments.GetOption("app-id"),
                cancellationToken)
            .ConfigureAwait(false);
        output.Write(
            new TestHistoryExportOutput(
                "ansight.agent-evaluation-bundle-export/v1",
                result),
            () => result.IsSuccess
                ? $"{result.Message} Bundle: {result.OutputPath}"
                : result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

internal static Task<int> RetryMeteringAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("CloudTestCommands.RetryMeteringAsync", [arguments, output, cancellationToken]);
    internal static string RenderHistory(WorkspaceTestHistoryResult history)
    {
        var lines = new List<string>
        {
            $"Test history: {history.Batches.Count:N0} batch(es), {history.Runs.Count:N0} execution(s)",
            $"History: {history.HistoryDirectoryPath}"
        };
        lines.AddRange(history.Batches.Select(entry =>
            $"BATCH\t{entry.Audit.BatchRunId}\t{entry.Audit.Status}\t{entry.Audit.StartedUtc:O}\t"
            + $"{entry.Audit.PassedCount} passed/{entry.Audit.FailedCount} failed/{entry.Audit.SkippedCount} skipped\t"
            + $"{entry.Audit.WorkspacePath}\t{entry.FilePath}"));
        lines.AddRange(history.Runs.Select(run =>
            $"RUN\t{run.RunId}\t{run.Status}\t{run.StartedUtc:O}\t"
            + $"{run.TestId ?? "ad-hoc"}\t{run.AppId ?? "unknown"}\t{run.FilePath}"
            + (string.IsNullOrWhiteSpace(run.BatchRunId) ? string.Empty : $"\tbatch={run.BatchRunId}")));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderHistoryInspection(WorkspaceTestHistoryInspection inspection)
    {
        var lines = new List<string>();
        if (inspection.Batch is null
            && inspection.Runs.Count > 0
            && inspection.Runs.All(entry => string.Equals(
                entry.Audit.BatchRunId,
                inspection.RequestedRunId,
                StringComparison.OrdinalIgnoreCase)))
        {
            lines.Add($"BATCH {inspection.RequestedRunId} · CORRELATED");
            lines.Add($"Executions: {inspection.Runs.Count:N0}");
        }

        if (inspection.Batch is { } batchEntry)
        {
            var batch = batchEntry.Audit;
            lines.Add($"BATCH {batch.BatchRunId} · {batch.Status.ToUpperInvariant()}");
            lines.Add(batch.Message);
            lines.Add($"Workspace: {batch.WorkspacePath}");
            lines.Add($"Source: {batch.Source} · Reasoning: {batch.Reasoning}");
            lines.Add($"Started: {batch.StartedUtc:O}");
            lines.Add($"Completed: {batch.CompletedUtc:O}");
            lines.Add($"Results: {batch.PassedCount} passed · {batch.FailedCount} failed · "
                      + $"{batch.SkippedCount} skipped · {batch.TotalTokens:N0} tokens");
            lines.AddRange(batch.Tests.Select(test =>
                $"TEST {test.Index}/{batch.Tests.Count}\t{test.Status.ToUpperInvariant()}\t"
                + $"{test.TestId}"
                + $"{RenderDeviceSuffix(test.Target?.DeviceIdentifier ?? test.RequestedDeviceIdentifier)}"
                + $"\t{test.Message}"
                + (string.IsNullOrWhiteSpace(test.AgentRunId) ? string.Empty : $"\trun={test.AgentRunId}")));
            lines.Add($"Audit: {batchEntry.FilePath}");
        }

        foreach (var entry in inspection.Runs)
        {
            var audit = entry.Audit;
            lines.Add($"RUN {audit.RunId} · {audit.Status.ToString().ToUpperInvariant()}");
            lines.Add($"Test: {audit.WorkspaceTestId ?? "ad-hoc"} · App: {audit.AppId ?? "unknown"}");
            lines.Add($"Session: {audit.SessionId} · Reasoning: {audit.Reasoning ?? "not recorded"}");
            lines.Add($"Execution: {audit.ModelPassCount:N0} model passes · "
                      + $"{audit.AnsightToolCallCount:N0} Ansight calls · {audit.DurationMilliseconds / 1000d:N1}s");
            lines.Add($"Tokens: {audit.Tokens.TotalTokens:N0} total · {audit.Tokens.InputTokens:N0} input · "
                      + $"{audit.Tokens.OutputTokens:N0} output");
            lines.AddRange(audit.Instructions.Select(instruction =>
                $"INSTRUCTION {instruction.Index}\t{instruction.Status.ToString().ToUpperInvariant()}\t{instruction.Summary}"));
            lines.AddRange(audit.ToolCalls.Select(call =>
                $"TOOL {call.Sequence}\t{(call.IsError ? "FAILED" : "OK")}\t{call.ToolName}\t"
                + $"{call.DurationMilliseconds:N0}ms\t{call.Message}"));
            lines.Add($"Audit: {entry.FilePath}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderCatalog(TestCatalogOutput result, bool validate)
    {
        var lines = new List<string>
        {
            validate
                ? result.IsValid
                    ? $"Validated {result.Tests.Count} workspace test(s)."
                    : $"Workspace test validation found {result.Warnings.Count} warning(s)."
                : $"Found {result.Tests.Count} workspace test(s)."
        };
        lines.AddRange(result.Tests.Select(test =>
            $"{test.TestId}\t{test.AppId}\t{test.Name}"
            + (test.Enabled ? string.Empty : "\tdisabled")
            + (string.IsNullOrWhiteSpace(test.TaskId) ? string.Empty : $"\ttask={test.TaskId}")
            + (test.RequiredSecrets.Count == 0
                ? string.Empty
                : $"\tsecrets={string.Join(',', test.RequiredSecrets)}")));
        lines.AddRange(result.Warnings.Select(warning => $"Warning: {warning}"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderRun(
        WorkspaceTestRunResult result,
        string resultFilePath,
        bool includeAudit,
        string? requestedTestId = null)
    {
        var trafficLight = result.IsSuccess ? "🟢" : result.IsSkipped ? "🟡" : "🔴";
        var status = result.IsSuccess ? "PASSED" : result.IsSkipped ? "SKIPPED" : "FAILED";
        var resolvedTestId = result.Test?.TestId ?? requestedTestId;
        var testLabel = result.Test is { } test
            ? $"{test.TestId} — {test.Name}"
            : resolvedTestId ?? "Workspace test";
        var lines = new List<string>
        {
            $"{trafficLight} {status}: {testLabel}",
            result.Message,
            $"Result: {resultFilePath}"
        };
        if (result.AgentResult is { } agentResult)
        {
            lines.Add($"Run: {agentResult.Audit.RunId}");
            if (!string.IsNullOrWhiteSpace(agentResult.Audit.BatchRunId))
            {
                lines.Add($"Batch: {agentResult.Audit.BatchRunId}");
            }
            if (includeAudit)
            {
                AppendAudit(lines, agentResult);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.SessionId))
        {
            lines.Add($"Session: {result.SessionId}");
        }

        if (result.Target is { } target)
        {
            lines.Add($"Target: {target.DeviceName} ({target.Platform}, {target.DeviceIdentifier})");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderBatch(
        WorkspaceTestBatchResult result,
        string resultFilePath,
        bool includeAudit)
    {
        var lines = new List<string>
        {
            $"Workspace tests complete: {result.PassedCount} passed, {result.FailedCount} failed, "
            + $"{result.SkippedCount} skipped{(result.WasCancelled ? ", cancelled" : string.Empty)}.",
            $"Result: {resultFilePath}"
        };
        if (!string.IsNullOrWhiteSpace(result.BatchRunId))
        {
            lines.Add($"Batch: {result.BatchRunId}");
        }
        if (!string.IsNullOrWhiteSpace(result.HistoryFilePath))
        {
            lines.Add($"History audit: {result.HistoryFilePath}");
        }
        if (!string.IsNullOrWhiteSpace(result.HistoryPersistenceError))
        {
            lines.Add($"History persistence failed: {result.HistoryPersistenceError}");
        }
        if (result.Results.Count == 0)
        {
            lines.Add("FAILED: No workspace tests were executed.");
        }
        lines.AddRange(result.Results
            .Where(static testResult => !string.IsNullOrWhiteSpace(testResult.SessionId))
            .Select(static testResult =>
                $"Session: {testResult.Test?.TestId ?? "unknown"} = {testResult.SessionId}"
                + RenderDeviceSuffix(testResult.Target?.DeviceIdentifier)));
        lines.AddRange(result.Results
            .Where(static testResult => !testResult.IsSuccess && !testResult.IsSkipped)
            .Select(static testResult =>
                $"FAILED: {testResult.Test?.TestId ?? "unknown"}: {testResult.Message}"
                + RenderDeviceSuffix(testResult.Target?.DeviceIdentifier)));
        lines.AddRange(result.Results
            .Where(static testResult => testResult.IsSkipped)
            .Select(static testResult =>
                $"SKIPPED: {testResult.Test?.TestId ?? "unknown"}: {testResult.Message}"
                + RenderDeviceSuffix(testResult.Target?.DeviceIdentifier)));
        if (includeAudit)
        {
            foreach (var testResult in result.Results.Where(static candidate => candidate.AgentResult is not null))
            {
                lines.Add($"Execution audit: {testResult.Test?.TestId ?? "unknown"}"
                          + RenderDeviceSuffix(testResult.Target?.DeviceIdentifier));
                AppendAudit(lines, testResult.AgentResult!);
            }
        }

        lines.Add(result.WasCancelled || result.FailedCount == 0 && result.SkippedCount > 0
            ? "🟡"
            : result.FailedCount > 0 || result.Results.Count == 0
                ? "🔴"
                : "🟢");

        return string.Join(Environment.NewLine, lines);
    }

    internal static string RenderDeviceSuffix(string? deviceIdentifier)
        => string.IsNullOrWhiteSpace(deviceIdentifier)
            ? string.Empty
            : $" [{deviceIdentifier}]";

    internal static void AppendAudit(List<string> lines, SimulatorAgentRunResult agentResult)
    {
        var audit = agentResult.Audit;
        lines.Add($"Audit trace: {agentResult.AuditFilePath ?? "not persisted"}");
        lines.Add(
            $"Reasoning: {audit.Reasoning ?? "not recorded"} ({audit.OpenAiTransport ?? "direct"}, {audit.OpenAiProtocol ?? "http"})");
        lines.Add(
            $"Tokens: {audit.Tokens.TotalTokens:N0} total; {audit.Tokens.InputTokens:N0} input; "
            + $"{audit.Tokens.OutputTokens:N0} output; {audit.Tokens.CachedInputTokens:N0} cached; "
            + $"{audit.Tokens.CacheWriteInputTokens:N0} cache-write; "
            + $"{audit.Tokens.ReasoningOutputTokens:N0} reasoning");
        lines.Add(
            $"Execution: {audit.ModelPassCount:N0} model passes; {audit.AnsightToolCallCount:N0} Ansight calls; "
            + $"{audit.DurationMilliseconds / 1000d:N1}s");
        lines.Add(
            audit.OpenAiTransport == "hosted-proxy"
                ? "Cost: attributed to this run in the organisation's provider-rate ledger; the CLI result records the exact token basis."
                : "Cost: exact token basis recorded; no provider rate card is available to the local CLI.");
    }

    internal static int ResolveRunExitCode(WorkspaceTestRunResult result)
    {
        if (result.IsSuccess)
        {
            return CliExitCodes.Success;
        }

        if (result.IsSkipped)
        {
            return CliExitCodes.CapabilityUnavailable;
        }

        if (result.Test is null || result.MissingSecrets.Count > 0)
        {
            return CliExitCodes.Configuration;
        }

        if (result.AgentResult is null
            && result.Message.Contains("waiting for", StringComparison.OrdinalIgnoreCase)
            && result.Message.Contains("session", StringComparison.OrdinalIgnoreCase))
        {
            return CliExitCodes.HostUnavailable;
        }

        if (result.AgentResult is null
            && (result.Message.Contains("simulator", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("emulator", StringComparison.OrdinalIgnoreCase))
            && (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)))
        {
            return CliExitCodes.CapabilityUnavailable;
        }

        if (result.AgentResult is null
            && (result.Message.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("organisation", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("API key", StringComparison.OrdinalIgnoreCase)))
        {
            return CliExitCodes.Configuration;
        }

        return CliExitCodes.TestFailed;
    }

    internal static bool ShouldWriteProgress(WorkspaceTestRunProgress progress, bool isVerbose)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return isVerbose
               || progress.AgentProgress?.Stage is not (
                   SimulatorAgentProgressStage.Thinking
                   or SimulatorAgentProgressStage.ModelCompleted);
    }

    internal static string BuildHelp()
        => """
           Discover, validate, run, and audit workspace tests

           Usage:
             ansight test list <workspace-path>
             ansight test validate <workspace-path>
             ansight test run <workspace-path> <test-id> [options]
             ansight test run-inline --session-id <id> --instruction <text> [options]
             ansight test run-all <workspace-path> [options]
             ansight test history [--app-id <id>] [--workspace <path>] [--batch <run-id>] [--limit <count>]
             ansight test inspect <run-id>
             ansight test export <run-id> <output.zip> [--app-id <id>]
             ansight test retry-metering <audit.json>

           Commands:
             list       List test definitions and configuration warnings
             validate   Validate all definitions; returns failure when warnings exist
             run        Run one end-to-end test
             run-inline Compatibility alias for ordered app execution; prefer `ansight app execute`
             run-all    Run every test, or tests selected with repeated --test options
             history    List persisted test batches and model/tool execution audits
             inspect    Inspect one persisted batch or execution audit
             export     Export trace JSON and associated portable session archives as one ZIP
             retry-metering  Retry one failed cloud-metering completion from its saved audit

           Target options:
             --session-id <id>            Reuse an already-connected app session
             --device-id <id>             Select a target; repeat to select multiple devices
             --device <id>                Alias for --device-id
             --device-kind <kind>         Limit discovery to physical or virtual targets
             --platform <ios|android>     Limit automatic target discovery
             --headless                   Do not open a native window for virtual targets
             --execution-mode sdk|device   SDK connection (default) or SDK-less simulator/emulator UI
             --app <path>                 Install an .app or .apk before launch
             --ipa <path>                 Install a signed physical-device IPA
             --application-path <path>    Generic alias for --app or --ipa
             --team-id <uuid>             Select the organisation used by hosted execution

           Inline runner options:
             --instruction <text>          Instruction to run; repeat to build an ordered sequence
             --instructions-file <path>    Read one non-empty instruction per line
             --secret <alias>              Grant a stored or same-named environment secret; may be repeated

           For new ad-hoc prompt-driven app actions, use:
             ansight app execute <session-id> --prompt <text>

           Runner options:
             --reasoning <mode>          Reasoning mode: fast (default), balanced, or deep
             --model-transport <mode>         Model connection: auto (default), websocket, or http
             --wait-seconds <seconds>     App-session connection timeout; default: 45
             --max-turns <count>          Maximum agent turns; default: 64
             --max-round-trips <count>    Maximum model round trips; default: 64
             --max-tool-calls <count>     Maximum tool calls; default: 512
             --batch <run-id>             Group an individual test run under this correlation ID
             --test <id>                  Select a test for run-all; may be repeated
             --parallel                   Run each selected test once across the selected devices
                                          Audio APIs reject parallel batches: select one device and run serially
             --stop-on-failure            Finish active tests, then do not start pending tests
             --no-workspace-tools         Do not expose trusted repository tasks to the agent
             --trace                      Capture full model context, tool payloads, and graph trace

           Output and audit:
             --result-file <path>         Write the versioned JSON result to this path
             --audit                      Print tokens, tool calls, timing, and cost attribution
             --json                       Emit the complete machine-readable result on stdout
             --silent                     Suppress all stdout and stderr output; overrides --json
             --verbose                    Include model progress, telemetry, and app events

           Secret resolution:
             Required aliases use the app-scoped secret store first, then environment variables
             with the exact alias name. Only aliases requested by the selected test(s) are read.

           Test history filters:
             --app-id <id>                Restrict execution history to one application ID
             --workspace <path>           Restrict batch and execution history to one workspace
             --batch <run-id>             Restrict history to one managed or correlated batch
             --limit <count>              Maximum batches and executions; default: 100

           The all-in-one runner does not build the app. Unless an app artifact is supplied, it
           launches the existing installed app, waits for its Ansight SDK session, and runs the test.
           A physical-target launch issues and injects a short-lived one-use enrollment payload.
           The app must opt in to unattended provisioning through its Ansight SDK configuration.
           After the test passes, fails, times out, or is cancelled, the runner stops the app it launched.

           Finding identifiers:
             ansight test list <workspace-path>  Workspace test IDs and their app IDs
             ansight device list                 Virtual and physical device IDs
             ansight session list --connected   Reusable live session IDs
             ansight app list                    App IDs known to Ansight
           """;

}
