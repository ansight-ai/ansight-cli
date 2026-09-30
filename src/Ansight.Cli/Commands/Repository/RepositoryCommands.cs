using Ansight.Host;

namespace Ansight.Cli.Commands.Repository;

internal static class RepositoryCommands
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

        var area = arguments.RequirePositional(1, "repository command").ToLowerInvariant();
        var isTaskRun = area == "task"
                        && arguments.Positionals.Count > 2
                        && string.Equals(
                            arguments.Positionals[2],
                            "run",
                            StringComparison.OrdinalIgnoreCase);
        if (isTaskRun)
        {
            arguments.EnsurePositionalCount(
                6,
                "ansight repo task run <app-id> <repository-path> <task-id> [options]");
            RepositoryTaskExecutionCommand.ValidateArguments(arguments);
        }

        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        if (isTaskRun)
        {
            return await RepositoryTaskExecutionCommand.RunAsync(
                lease.Runtime,
                arguments.RequirePositional(3, "app identifier"),
                Path.GetFullPath(arguments.RequirePositional(4, "repository path")),
                arguments.RequirePositional(5, "task identifier"),
                arguments,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        return area switch
        {
            "tasks" or "task" => InspectTasks(lease.Runtime, arguments, output),
            "automation" or "automations" or "trigger" or "triggers" => RunAutomation(
                lease.Runtime,
                arguments,
                output),
            _ => throw new CliUsageException(
                $"Unknown repository command '{area}'. Expected tasks, task run, or automation.")
        };
    }

    private static int InspectTasks(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight repo tasks <app-id> <repository-path>");
        var appId = arguments.RequirePositional(2, "app identifier");
        var repositoryPath = Path.GetFullPath(arguments.RequirePositional(3, "repository path"));
        var catalog = runtime.InspectRepositoryTasks(appId, repositoryPath);
        output.Write(
            new RepositoryTaskCatalogOutput("ansight.repository-tasks/v1", catalog),
            () => catalog.Tasks.Count == 0
                ? $"No repository tasks found. {string.Join(' ', catalog.Warnings)}"
                : string.Join(
                    Environment.NewLine,
                    catalog.Tasks.Select(task =>
                        $"{task.TaskId}\t{task.Title}\t{task.Description}"
                        + (task.Enabled ? string.Empty : "\tdisabled")))
                  + RenderWarnings(catalog.Warnings));
        return catalog.Warnings.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static int RunAutomation(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        var action = arguments.RequirePositional(2, "automation action").ToLowerInvariant();
        return action switch
        {
            "inspect" => InspectAutomation(runtime, arguments, output),
            "connect" => ConnectAutomation(runtime, arguments, output),
            "disconnect" => DisconnectAutomation(runtime, arguments, output),
            "list" => ListAutomations(runtime, arguments, output),
            "runs" or "history" => ListRuns(runtime, arguments, output),
            _ => throw new CliUsageException(
                $"Unknown repository automation action '{action}'. Expected inspect, connect, disconnect, list, or runs.")
        };
    }

    private static int InspectAutomation(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            5,
            "ansight repo automation inspect <app-id> <repository-path>");
        var result = runtime.RepositoryAutomations.Inspect(
            arguments.RequirePositional(3, "app identifier"),
            Path.GetFullPath(arguments.RequirePositional(4, "repository path")));
        return WriteConnection("inspect", result, output);
    }

    private static int ConnectAutomation(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            5,
            "ansight repo automation connect <app-id> <repository-path>");
        if (CliCommandContext.Current is null)
        {
            throw new CliHostUnavailableException(
                "Repository trigger connections require a resident host.");
        }

        var result = runtime.RepositoryAutomations.Connect(
            arguments.RequirePositional(3, "app identifier"),
            Path.GetFullPath(arguments.RequirePositional(4, "repository path")));
        return WriteConnection("connect", result, output);
    }

    private static int DisconnectAutomation(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight repo automation disconnect <app-id>");
        if (CliCommandContext.Current is null)
        {
            throw new CliHostUnavailableException(
                "Repository trigger disconnection requires a resident host.");
        }

        var appId = arguments.RequirePositional(3, "app identifier");
        var disconnected = runtime.RepositoryAutomations.Disconnect(appId);
        output.Write(
            new RepositoryAutomationOperationOutput(
                "ansight.repository-automation-operation/v1",
                "disconnect",
                appId,
                disconnected),
            () => disconnected
                ? $"Disconnected repository automations for '{appId}'."
                : $"No repository automations were connected for '{appId}'.");
        return disconnected ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static int ListAutomations(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight repo automation list");
        var triggers = runtime.RepositoryAutomations.GetTriggers();
        output.Write(
            new RepositoryAutomationListOutput("ansight.repository-automations/v1", triggers),
            () => triggers.Count == 0
                ? "No repository automations are connected."
                : string.Join(
                    Environment.NewLine,
                    triggers.Select(trigger =>
                        $"{trigger.AppId}\t{trigger.TriggerId}\t{trigger.EventKind}\t{trigger.ActionKind}"
                        + (trigger.Enabled ? string.Empty : "\tdisabled"))));
        return CliExitCodes.Success;
    }

    private static int ListRuns(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight repo automation runs <app-id> [--limit <count>]");
        var appId = arguments.RequirePositional(3, "app identifier");
        var runs = runtime.RepositoryAutomations.GetRecentRuns(
            appId,
            arguments.GetIntOption("limit", 100, 1, 500));
        output.Write(
            new RepositoryAutomationRunsOutput(
                "ansight.repository-automation-runs/v1",
                appId,
                runs),
            () => runs.Count == 0
                ? $"No repository automation runs found for '{appId}'."
                : string.Join(
                    Environment.NewLine,
                    runs.Select(run =>
                        $"{run.RunId}\t{run.Status}\t{run.TriggerId}\t{run.CompletedAtUtc:O}")));
        return CliExitCodes.Success;
    }

    private static int WriteConnection(
        string operation,
        RepositoryAutomationConnection result,
        CliOutput output)
    {
        output.Write(
            new RepositoryAutomationConnectionOutput(
                "ansight.repository-automation-connection/v1",
                operation,
                result),
            () => $"Repository automations: available={result.IsAvailable}, connected={result.IsConnected}, "
                  + $"triggers={result.Triggers.Count}"
                  + RenderWarnings(result.Warnings));
        return result.IsAvailable && (operation != "connect" || result.IsConnected)
            ? result.Warnings.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure
            : CliExitCodes.Failure;
    }

    private static string RenderWarnings(IReadOnlyList<string> warnings)
        => warnings.Count == 0
            ? string.Empty
            : Environment.NewLine
              + string.Join(Environment.NewLine, warnings.Select(warning => $"Warning: {warning}"));

    private static string BuildHelp()
        => """
           Inspect and run repository tasks, and manage event-driven automations

           Usage:
             ansight repo tasks <app-id> <repository-path>
             ansight repo task run <app-id> <repository-path> <task-id> [options]
             ansight repo automation inspect <app-id> <repository-path>
             ansight repo automation connect <app-id> <repository-path>
             ansight repo automation disconnect <app-id>
             ansight repo automation list
             ansight repo automation runs <app-id> [--limit <count>]

           Commands:
             tasks                  Discover reusable agent tasks declared by a repository
             task run               Run one task against a connected session for an app
             automation inspect     Validate triggers without connecting them
             automation connect     Connect trusted triggers to the resident host
             automation disconnect  Disconnect triggers for one application
             automation list        List currently connected triggers
             automation runs        Show recent trigger executions; alias: history

           Aliases:
             repository = repo
             automations, trigger, triggers = automation

           Task run options:
             --headless              Do not open windows for device starts during execution
             --session-id <id>       Target an exact connected session; defaults to the latest for the app
             --device-id <id>        Target the connected session on an exact simulator/emulator
             --input <JSON>          Pass task input as a JSON object
             --input-file <path>     Read task input from a JSON file

           Task execution requires a resident host and a connected SDK session. Start
           `ansight host run`, open the app in a simulator, then invoke the task command.

           Linked app workspaces restore their trigger connections when the resident host starts.
           Use `--disable-repository-automations` to start a host without trigger execution.
           Inspection does not execute triggers.
           """;

}
