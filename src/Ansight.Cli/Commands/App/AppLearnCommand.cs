using System.Text;
using System.Text.Json;
using Ansight.Cli.Commands.App.Learning;
using Ansight.Host.Explorer.TaskExtraction;

namespace Ansight.Cli.Commands.App;

internal static class AppLearnCommand
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] valueOptions =
    [
        "session-id", "focus", "max-actions", "max-tasks", "reasoning", "model", "model-transport",
        "wait-seconds", "device-id", "platform", "result-file",
        "data-dir", "adb-path", "xcode-path", "secret-store-file", "secret-key-file", "discovery-port", "websocket-port", "node-path"
    ];
    private static readonly string[] flagOptions =
    ["draft-only", "launch", "no-launch", "headless", "json", "silent", "verbose", "trace", "no-trace", "help", "beta"];

    internal static void ValidateArguments(CliArguments arguments)
    {
        arguments.EnsurePositionalCount(3, "ansight app learn <app-id> [options]");
        if (string.IsNullOrWhiteSpace(arguments.RequirePositional(2, "App ID")))
            throw new CliUsageException("An exact App ID is required.");
        arguments.EnsureOptionContract("app learn", flagOptions, valueOptions);
        _ = arguments.GetIntOption("max-tasks", 3, 1, 10);
        _ = arguments.GetIntOption("max-actions", 64, 1, 128);
        _ = ReasoningOptions.Resolve(arguments);
        _ = ReasoningOptions.ResolveModelOverride(arguments);
        _ = ModelTransportOptions.ResolveModelTransport(arguments);
        if (arguments.HasFlag("launch") && arguments.HasFlag("no-launch"))
            throw new CliUsageException("Use only one of --launch or --no-launch.");
        if (arguments.HasFlag("wait-seconds")) _ = arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45));
        if (arguments.GetOption("result-file") is { } file && File.Exists(Path.GetFullPath(file)))
            throw new CliUsageException("The learning report already exists. Choose a new --result-file path.");
    }

    public static async Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken)
    {
        ValidateArguments(arguments);
        var appId = arguments.RequirePositional(2, "App ID").Trim();
        await using var lease = await CliRuntimeLease.CreateAsync(CliRuntime.ResolveOptions(arguments), true, cancellationToken)
            .ConfigureAwait(false);
        var runtime = lease.Runtime;
        var workspace = runtime.Apps.Get(appId)?.CodebasePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            throw new CliUsageException($"Link '{appId}' to its workspace first: ansight app register {appId} --codebase <path>.");
        workspace = Path.GetFullPath(workspace);
        TaskDraftAuthor.ValidateEnvironment(runtime);
        var existing = runtime.InspectRepositoryTasks(appId, workspace);
        if (existing.Warnings.Count > 0)
            throw new CliUsageException("Resolve workspace task diagnostics before learning: " + string.Join(" ", existing.Warnings));

        var explorationArguments = BuildExplorationArguments(arguments, appId);
        AppGraphCommands.ValidateArguments(explorationArguments, "explore", 1);
        output.WriteProgress($"[learn.explore] Exploring '{appId}' with a {arguments.GetIntOption("max-actions", 64, 1, 128)}-action limit.");
        AppGraphExploreOutput? exploration = null;
        var explorationExit = await AppGraphCommands.ExploreAsync(runtime, appId, explorationArguments, output,
            cancellationToken, result => exploration = result,
            "For task learning, put the destination actually visible at the start of this run FIRST in the final destinations array. "
            + "Use that observed screen or named state, not an inferred navigation host. Preserve this order when merging earlier graph evidence.",
            reserveSession: true)
            .ConfigureAwait(false);
        if (exploration is null) return explorationExit;

        var plan = AppLearnPlanner.Build(exploration.CandidateDefinition, exploration.Evidence,
            existing.Tasks.Select(task => task.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            arguments.GetIntOption("max-tasks", 3, 1, 10));
        var runDirectory = Path.Combine(runtime.BaseFolderPath, "app-learn-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var reportPath = Path.GetFullPath(arguments.GetOption("result-file") ?? Path.Combine(runDirectory, "report.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using (File.Open(reportPath, FileMode.CreateNew, FileAccess.Write)) { }
        var tasks = new List<AppLearnTaskResult>();
        var report = new AppLearnReport("ansight.app-learn/v1", appId, workspace, reportPath,
            arguments.HasFlag("draft-only"), "running", exploration, plan.Diagnostics, tasks);
        await SaveReportAsync(report).ConfigureAwait(false);
        try
        {
            if (explorationExit != CliExitCodes.Success)
            {
                report = report with { Status = "failed" };
                return explorationExit;
            }
            if (plan.Candidates.Count == 0)
            {
                report = report with { Status = "no-candidates" };
                return CliExitCodes.Success;
            }
            var liveRun = runtime.SimulatorAgent.GetAppGraphLiveRun(exploration.AuditRunId)
                ?? throw new InvalidDataException("The exploration evidence interval is unavailable; refusing to author from unrelated history.");
            using var author = new AppLearnAuthor(runtime, output);
            foreach (var candidate in plan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.WriteProgress($"[learn.candidate] {candidate.To}: {candidate.Reason}");
                var request = new LocalTaskExtractionStartRequest(exploration.SessionId, liveRun.StartedUtc,
                    liveRun.CompletedUtc ?? liveRun.UpdatedUtc, AppLearnPrompts.Author(candidate), candidate.TaskName)
                {
                    Reasoning = ReasoningOptions.Resolve(arguments),
                    Model = ReasoningOptions.ResolveModelOverride(arguments)
                };
                LocalTaskExtractionSnapshot? checkpoint = null;
                AppLearnTaskResult result;
                try
                {
                    result = await AppLearnPipeline.RunCandidateAsync(candidate, request, report.DraftOnly, author,
                        PrepareAsync, cancellationToken, snapshot => checkpoint = snapshot).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    tasks.Add(new(candidate, "cancelled", "Learning was cancelled; any completed draft is retained.", checkpoint));
                    throw;
                }
                tasks.Add(result);
                output.WriteProgress($"[learn.{result.Status}] {candidate.TaskId}: {result.Message}");
                await SaveReportAsync(report).ConfigureAwait(false);
            }
            report = report with { Status = tasks.Any(task => task.Status is not ("saved" or "draft")) ? "partial"
                : tasks.Count == 0 ? "no-candidates" : "completed" };
            return report.Status == "partial" ? CliExitCodes.Failure : CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report = report with { Status = "cancelled" };
            return CliExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        {
            report = report with { Status = "failed", Diagnostics = [.. report.Diagnostics, exception.Message] };
            return CliExitCodes.Failure;
        }
        finally
        {
            await SaveReportAsync(report).ConfigureAwait(false);
            output.Write(report, () => $"Learning {report.Status}: {tasks.Count(task => task.Status == "saved")} verified task(s) saved, "
                + $"{tasks.Count(task => task.Status != "saved" && task.Extraction?.Draft is not null)} draft(s) retained.\nReport: {reportPath}"
                + (tasks.Count == 0 ? "\n" + string.Join("\n", plan.Diagnostics) : string.Empty));
        }

        async Task<bool> PrepareAsync(AppLearnCandidate candidate, LocalTaskExtractionSnapshot extraction, CancellationToken token)
        {
            var task = runtime.InspectRepositoryTasks(appId, extraction.Draft!.DraftRootPath).Tasks.Single();
            var preparationArguments = BuildPreparationArguments(arguments, exploration.SessionId);
            var progressOutput = new CliOutput(false, TextWriter.Null, new ProgressWriter(output));
            output.WriteProgress($"[learn.prepare] Establishing '{candidate.From}' before testing '{candidate.TaskId}'.");
            return await AppExecutionService.RunAsync(preparationArguments, progressOutput,
                new AppExecutionTarget(exploration.SessionId, appId, null),
                [AppLearnPrompts.Prepare(candidate, task.StartingState!)], false, token).ConfigureAwait(false) == CliExitCodes.Success;
        }
    }

    internal static CliArguments BuildExplorationArguments(CliArguments arguments, string appId)
    {
        var actions = arguments.GetIntOption("max-actions", 64, 1, 128);
        var values = new List<string> { "app-graph", "explore", appId,
            "--max-actions", actions.ToString(), "--max-turns", (actions * 4).ToString(),
            "--max-round-trips", (actions * 4).ToString(), "--max-tool-calls", (actions * 8).ToString() };
        CopyOptions(arguments, values, ["session-id", "focus", "reasoning", "model", "model-transport", "wait-seconds", "device-id", "platform"],
            ["launch", "no-launch", "headless", "verbose", "trace", "no-trace"]);
        return CliArguments.Parse(values);
    }

    internal static CliArguments BuildPreparationArguments(CliArguments arguments, string sessionId)
    {
        var values = new List<string> { "app", "execute", sessionId,
            "--max-turns", "32", "--max-round-trips", "32", "--max-tool-calls", "96" };
        CopyOptions(arguments, values, ["reasoning", "model", "model-transport", "data-dir", "adb-path", "xcode-path",
            "secret-store-file", "secret-key-file", "discovery-port", "websocket-port", "node-path"], ["verbose", "trace", "no-trace"]);
        return CliArguments.Parse(values);
    }

    private static void CopyOptions(CliArguments source, List<string> destination, string[] values, string[] flags)
    {
        foreach (var name in values)
            if (source.GetOption(name) is { } value) destination.AddRange(["--" + name, value]);
        foreach (var name in flags)
            if (source.HasFlag(name)) destination.Add("--" + name);
    }

    private static async Task SaveReportAsync(AppLearnReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report.ReportPath)!);
        var temporary = report.ReportPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, jsonOptions)).ConfigureAwait(false);
            File.Move(temporary, report.ReportPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class ProgressWriter(CliOutput output) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) { if (value is not null) output.WriteProgress(value); }
    }

    internal const string Help = """
        ansight app learn <app-id> [options]

        Explore reversible UI navigation, rank reusable routes, author tasks from recorded evidence,
        restore each starting state, and save only drafts that pass live assertions. Requires an SDK
        session, a linked workspace, and the same AI gateway configuration as App Graph exploration.

          --session-id <id>        Select an exact live session for this app
          --launch                Launch the registered app if it is not connected
          --device-id <id>         Device to use when launching
          --platform ios|android  Platform to use when launching
          --wait-seconds <n>       Wait for launch connection (default 45)
          --focus <text>          Guide exploration toward a feature
          --max-actions <n>       Exploration action limit (default 64, maximum 128)
          --max-tasks <n>         Maximum task candidates (default 3, maximum 10)
          --draft-only            Author and statically validate; do not replay or save tasks
          --reasoning <mode>      fast (default), balanced, or deep
          --result-file <path>    New JSON report path (default: host app-learn-runs directory)
          --headless              Do not open a simulator window when launching
          --json                  Emit one versioned learning report

        Tasks use fixed observed inputs. Ranking estimates reuse from route length and shared setup;
        it does not measure user repetition. Existing tasks are preserved. Drafts, failures, source
        evidence, and assertions are linked from the report. The app remains open after learning.

        Example:
          ansight app learn com.example.app --focus "Search and details" --max-tasks 3 --json
        """;
}
