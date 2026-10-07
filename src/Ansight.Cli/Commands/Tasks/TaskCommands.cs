using Ansight.Host;
using Ansight.Host.SimulatorAgent;
using System.Globalization;
using System.Text;

namespace Ansight.Cli.Commands.Tasks;

internal static class TaskCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.RequirePositional(1, "task action").ToLowerInvariant();
        if (action == "run")
        {
            arguments.EnsurePositionalCount(
                3,
                "ansight task run <task-id> --app-id <app-id> [options]");
            _ = arguments.RequireOption("app-id");
            RepositoryTaskExecutionCommand.ValidateArguments(arguments);
        }

        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        return action switch
        {
            "list" => List(lease.Runtime, arguments, output),
            "run" => await RunTaskAsync(
                lease.Runtime,
                arguments,
                output,
                cancellationToken).ConfigureAwait(false),
            "extract" => await ExtractAsync(
                lease.Runtime,
                arguments,
                output,
                cancellationToken).ConfigureAwait(false),
            _ => throw new CliUsageException(
                $"Unknown task action '{action}'. Expected list, run, or extract.")
        };
    }

    private static int List(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            2,
            "ansight task list --app-id <app-id> [--repository <path>]");
        var appId = arguments.RequireOption("app-id");
        var repositoryPath = ResolveRepositoryPath(runtime, arguments, appId);
        var catalog = runtime.InspectRepositoryTasks(appId, repositoryPath);
        output.Write(
            new RepositoryTaskCatalogOutput("ansight.repository-tasks/v1", catalog),
            () => catalog.Tasks.Count == 0
                ? $"No tasks found. {string.Join(' ', catalog.Warnings)}"
                : string.Join(
                    Environment.NewLine,
                    catalog.Tasks.Select(task =>
                        $"{task.TaskId}\t{task.Title}\t{task.Description}"
                        + (task.Enabled ? string.Empty : "\tdisabled")))
                  + RenderWarnings(catalog.Warnings));
        return catalog.Warnings.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static Task<int> RunTaskAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight task run <task-id> --app-id <app-id> [options]");
        var appId = arguments.RequireOption("app-id");
        return RepositoryTaskExecutionCommand.RunAsync(
            runtime,
            appId,
            ResolveRepositoryPath(runtime, arguments, appId),
            arguments.RequirePositional(2, "task identifier"),
            arguments,
            output,
            cancellationToken);
    }

    private static async Task<int> ExtractAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight task extract <session-id> --start <timestamp|offset> --end <timestamp|offset> [--format ansight|test|maestro|appium] [--workspace <path>] [--title <title>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            output.WriteError("session_not_found", $"Session '{sessionId}' was not found.", CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        var startUtc = ParseTimelinePosition(arguments.RequireOption("start"), snapshot, "start");
        var endUtc = ParseTimelinePosition(arguments.RequireOption("end"), snapshot, "end");
        var title = arguments.GetOption("title")
                    ?? snapshot.Name
                    ?? $"Recorded {snapshot.AppId} workflow";
        var format = arguments.GetOption("format")?.Trim().ToLowerInvariant() ?? "ansight";
        if (format is not ("ansight" or "test" or "maestro" or "appium"))
        {
            throw new CliUsageException("--format must be 'ansight', 'test', 'maestro', or 'appium'.");
        }
        if (format != "test" && (arguments.GetOptions("assertion").Count > 0 || arguments.GetOption("validation") is not null))
        {
            throw new CliUsageException("--assertion and --validation require --format test.");
        }
        if (format != "test" && arguments.GetOptions("task-section").Count > 0)
        {
            throw new CliUsageException("--task-section requires --format test.");
        }

        var selectedTaskSections = arguments.GetOptions("task-section");
        if (format == "test" && arguments.HasFlag("ai") && selectedTaskSections.Count == 0)
        {
            selectedTaskSections = snapshot.Annotations
                .Where(annotation => annotation.EndUtc > annotation.StartUtc
                    && annotation.StartUtc >= startUtc
                    && annotation.EndUtc <= endUtc
                    && !string.IsNullOrWhiteSpace(annotation.Label))
                .OrderBy(annotation => annotation.StartUtc)
                .Select(annotation => annotation.AnnotationId)
                .ToArray();
        }

        var extraction = format == "ansight"
            ? TimelineTaskExtractor.Extract(snapshot, startUtc, endUtc, title)
            : null;
        var maestro = format == "maestro"
            ? MaestroFlowExtractor.Extract(snapshot, startUtc, endUtc, title)
            : null;
        var appium = format == "appium"
            ? AppiumScriptExtractor.Extract(snapshot, startUtc, endUtc, title)
            : null;
        var test = format == "test"
            ? WorkspaceTestExtractor.Extract(
                snapshot, startUtc, endUtc, title,
                arguments.GetOptions("assertion"), arguments.GetOption("validation"), selectedTaskSections)
            : null;
        var workspacePath = Path.GetFullPath(
            arguments.GetOption("workspace")
            ?? arguments.GetOption("repository")
            ?? Environment.CurrentDirectory);
        var outputPath = Path.GetFullPath(
            arguments.GetOption("output")
            ?? (format switch
            {
                "maestro" => Path.Combine(workspacePath, ".maestro", $"{maestro!.SuggestedName}.yaml"),
                "appium" => Path.Combine(workspacePath, "appium", $"{appium!.SuggestedName}.test.mjs"),
                "test" => Path.Combine(workspacePath, "ansight", "tests", $"{test!.SuggestedName}.yaml"),
                _ => Path.Combine(workspacePath, "ansight", "tasks", $"{extraction!.SuggestedName}.ts")
            }));

        if (File.Exists(outputPath) && !arguments.HasFlag("force") && !arguments.HasFlag("overwrite"))
        {
            throw new CliUsageException(
                $"Extraction file '{outputPath}' already exists. Pass --force to replace it or --output to choose another path.");
        }

        string? refinementModel = null;
        if (arguments.HasFlag("ai"))
        {
            if (maestro is not null)
            {
                var refinement = await MaestroFlowRefiner.RefineAsync(
                    runtime,
                    snapshot,
                    startUtc,
                    endUtc,
                    title,
                    maestro.Source,
                    arguments.GetOption("reasoning") ?? AgentReasoningModes.Fast,
                    arguments.GetOption("model") ?? string.Empty,
                    workspacePath,
                    cancellationToken).ConfigureAwait(false);
                maestro = maestro with { Source = refinement.Source };
                refinementModel = refinement.Model;
            }
            else if (test is not null)
            {
                test = await WorkspaceTestRefiner.RefineAsync(
                    runtime,
                    snapshot,
                    startUtc,
                    endUtc,
                    title,
                    test,
                    selectedTaskSections,
                    arguments.GetOption("reasoning") ?? AgentReasoningModes.Fast,
                    arguments.GetOption("model"),
                    workspacePath,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new CliUsageException("--ai requires --format maestro or --format test.");
            }
        }
        var directoryPath = Path.GetDirectoryName(outputPath)
                            ?? throw new IOException($"Unable to resolve the parent directory for '{outputPath}'.");
        Directory.CreateDirectory(directoryPath);
        var temporaryPath = Path.Combine(directoryPath, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    maestro?.Source ?? appium?.Source ?? test?.Source ?? extraction!.Source,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        if (maestro is not null)
        {
            output.Write(
                new MaestroFlowExtractionOutput(
                    "ansight.maestro-flow-extraction/v1",
                    snapshot.SessionId,
                    startUtc,
                    endUtc,
                    outputPath,
                    maestro,
                    refinementModel),
                () => $"Generated {maestro.GeneratedActionCount:N0} Maestro action(s).{Environment.NewLine}Flow: {outputPath}"
                      + (maestro.Diagnostics.Count == 0
                          ? string.Empty
                          : Environment.NewLine + string.Join(
                              Environment.NewLine,
                              maestro.Diagnostics.Select(static diagnostic => $"Review: {diagnostic}"))));
            return CliExitCodes.Success;
        }

        if (appium is not null)
        {
            output.Write(
                new AppiumScriptExtractionOutput(
                    "ansight.appium-script-extraction/v1",
                    snapshot.SessionId,
                    startUtc,
                    endUtc,
                    outputPath,
                    appium),
                () => $"Generated {appium.GeneratedActionCount:N0} Appium action(s).{Environment.NewLine}Script: {outputPath}"
                      + (appium.Diagnostics.Count == 0
                          ? string.Empty
                          : Environment.NewLine + string.Join(
                              Environment.NewLine,
                              appium.Diagnostics.Select(static diagnostic => $"Review: {diagnostic}"))));
            return CliExitCodes.Success;
        }

        if (test is not null)
        {
            output.Write(
                new WorkspaceTestExtractionOutput(
                    "ansight.workspace-test-extraction/v1",
                    snapshot.SessionId,
                    startUtc,
                    endUtc,
                    outputPath,
                    test),
                () => $"Generated {test.GeneratedActionCount:N0} recorded step(s).{Environment.NewLine}Test: {outputPath}"
                      + (test.Diagnostics.Count == 0
                          ? string.Empty
                          : Environment.NewLine + string.Join(
                              Environment.NewLine,
                              test.Diagnostics.Select(static diagnostic => $"Review: {diagnostic}"))));
            return CliExitCodes.Success;
        }

        var result = new TimelineTaskExtractionOutput(
            "ansight.timeline-task-extraction/v1",
            snapshot.SessionId,
            startUtc,
            endUtc,
            outputPath,
            extraction!);
        output.Write(
            result,
            () => $"{extraction!.Summary}{Environment.NewLine}Task: {outputPath}"
                  + (extraction.Diagnostics.Count == 0
                      ? string.Empty
                      : Environment.NewLine + string.Join(
                          Environment.NewLine,
                          extraction.Diagnostics.Select(static diagnostic => $"Review: {diagnostic}"))));
        return CliExitCodes.Success;
    }

    private static DateTimeOffset ParseTimelinePosition(
        string value,
        AppSessionSnapshot snapshot,
        string optionName)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('+'))
        {
            normalized = normalized[1..];
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0)
        {
            return snapshot.CreatedUtc.ToUniversalTime().AddSeconds(seconds);
        }

        throw new CliUsageException(
            $"--{optionName} must be an ISO-8601 timestamp or a non-negative seconds offset from session start.");
    }

    private static string ResolveRepositoryPath(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        string appId)
        => Path.GetFullPath(
            arguments.GetOption("repository")
            ?? runtime.Apps.Get(appId)?.CodebasePath
            ?? Environment.CurrentDirectory);

    private static string RenderWarnings(IReadOnlyList<string> warnings)
        => warnings.Count == 0
            ? string.Empty
            : Environment.NewLine
              + string.Join(Environment.NewLine, warnings.Select(warning => $"Warning: {warning}"));

    private static string BuildHelp()
        => """
           Discover and run repeatable Ansight tasks

           Usage:
             ansight task list --app-id <app-id> [--repository <path>]
             ansight task run <task-id> --app-id <app-id> [options]
             ansight task extract <session-id> --start <timestamp|offset> --end <timestamp|offset> [options]

           Options:
             --headless              Do not open windows for device starts during execution
             --app-id <id>           Application bundle/package ID
             --repository <path>     Override the app's registered codebase; unregistered apps use the current directory
             --device-id <id>        Target a connected simulator/emulator by device identifier
             --session-id <id>       Target an exact connected Ansight session
             --input <JSON>          Pass task input as a JSON object
             --input-file <path>     Read task input from a JSON file
             --secret <alias>        Grant a named secret to this task; may be repeated
             --json                  Emit the complete versioned task result

           Extraction options:
             --start <value>         ISO-8601 timestamp or seconds after session start
             --end <value>           ISO-8601 timestamp or seconds after session start
             --workspace <path>      Workspace root; defaults to the current directory
             --title <title>         Task title; defaults to the session or app name
             --format <format>       ansight (default), test, maestro, or appium
             --assertion <text>      Final-state assertion for --format test; repeatable
             --validation <text>     Final-state validation instructions for --format test
             --task-section <id>    Use a recorded annotation section for --format test; repeatable
             --ai                    Refine a Maestro or Ansight test draft with the configured AI model
             --reasoning <mode>      fast, balanced, or deep for --ai
             --model <model>         Optional model override for --ai
             --output <path>         Explicit output file destination
             --force                 Replace an existing extraction file

           Use only one of --device-id or --session-id. Task execution requires a
           resident `ansight host run` process and an app session already connected
           on the selected device.

           Find device identifiers with `ansight devices list`.

           Aliases:
             tasks = task
             `ansight repo tasks` and `ansight repo task run` remain supported
           """;
}

internal sealed record TimelineTaskExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    TimelineTaskExtraction Extraction);

internal sealed record MaestroFlowExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    MaestroFlowExtraction Extraction,
    string? Model);

internal sealed record AppiumScriptExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    AppiumScriptExtraction Extraction);

internal sealed record WorkspaceTestExtractionOutput(
    string Schema,
    string SessionId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string FilePath,
    WorkspaceTestExtraction Extraction);
