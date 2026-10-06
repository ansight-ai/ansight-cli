using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Workspace;

internal static class WorkspaceCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.RequirePositional(1, "workspace action").ToLowerInvariant();
        if (action == "list")
        {
            return await ListAsync(arguments, output, cancellationToken).ConfigureAwait(false);
        }

        var service = new WorkspaceAuthoringService();
        try
        {
            if (action is "init" or "initialize")
            {
                return await InitializeAsync(
                    service,
                    arguments,
                    output,
                    cancellationToken).ConfigureAwait(false);
            }

            var result = action switch
            {
                "add" or "create" => Add(service, arguments),
                _ => throw new CliUsageException(
                    $"Unknown workspace action '{action}'. Expected list, init, or add.")
            };
            output.Write(
                new WorkspaceAuthoringOutput(
                    "ansight.workspace-authoring/v1",
                    action,
                    result),
                () => Render(result));
            return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException(exception.Message);
        }
    }

    private static async Task<int> ListAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(2, "ansight workspace list");
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        var workspaces = lease.Runtime.Apps.List()
            .Where(static app => !string.IsNullOrWhiteSpace(app.CodebasePath))
            .Select(static app => new LinkedWorkspaceDescriptor(
                app.AppId,
                app.Name,
                app.CodebasePath!,
                app.AutomaticTrendsMonitoringEnabled,
                app.RepositoryAutomationsEnabled))
            .ToArray();
        output.Write(
            new WorkspaceListOutput("ansight.workspaces/v1", workspaces),
            () => workspaces.Length == 0
                ? "No linked workspaces."
                : string.Join(
                    Environment.NewLine,
                    workspaces.Select(workspace =>
                        $"{workspace.AppId}\t{workspace.AppName}\t{workspace.CodebasePath}")));
        return CliExitCodes.Success;
    }

    private static async Task<int> InitializeAsync(
        WorkspaceAuthoringService service,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight workspace init <workspace-path> [--app-id <id>] [--name <name>] [--no-register] [--force]");
        var appId = arguments.GetOption("app-id");
        if (arguments.HasFlag("no-register") && appId is not null)
        {
            throw new CliUsageException("Use either --app-id or --no-register, not both.");
        }

        var result = service.Initialize(new WorkspaceInitializeRequest(
            arguments.RequirePositional(2, "workspace path"),
            HasOverwriteFlag(arguments)));
        AppOperationResult? appRegistration = null;
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(appId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = CliRuntime.ResolveOptions(arguments);
            await using var lease = await CliRuntimeLease.CreateAsync(
                options,
                start: false,
                cancellationToken).ConfigureAwait(false);
            appRegistration = lease.Runtime.Apps.Register(new AppRegistrationRequest(
                appId,
                arguments.GetOption("name"),
                result.WorkspacePath));
        }

        var registrationNextStep = appRegistration is null && result.IsSuccess
            ? $"ansight app register <app-id> --codebase \"{result.WorkspacePath}\""
            : null;
        output.Write(
            new WorkspaceAuthoringOutput(
                "ansight.workspace-authoring/v1",
                "init",
                result,
                appRegistration,
                registrationNextStep),
            () => Render(result, appRegistration, registrationNextStep));
        return result.IsSuccess && appRegistration?.IsSuccess is not false
            ? CliExitCodes.Success
            : CliExitCodes.Failure;
    }

    private static WorkspaceAuthoringResult Add(
        WorkspaceAuthoringService service,
        CliArguments arguments)
    {
        var kind = arguments.RequirePositional(2, "definition kind").ToLowerInvariant();
        arguments.EnsurePositionalCount(
            5,
            "ansight workspace add <task|test|trigger|sanitizer> <workspace-path> <id> [options]");
        return kind switch
        {
            "task" or "tasks" => AddTask(service, arguments),
            "test" or "tests" => AddTest(service, arguments),
            "trigger" or "triggers" or "automation" => AddTrigger(service, arguments),
            "sanitizer" or "sanitizers" or "sanitiser" or "sanitisers" => AddSanitizer(service, arguments),
            _ => throw new CliUsageException(
                $"Unknown workspace definition kind '{kind}'. Expected task, test, trigger, or sanitizer.")
        };
    }

    private static WorkspaceAuthoringResult AddTask(
        WorkspaceAuthoringService service,
        CliArguments arguments)
        => service.AddTask(new WorkspaceTaskCreateRequest(
            arguments.RequirePositional(3, "workspace path"),
            arguments.RequirePositional(4, "task identifier"),
            arguments.GetOption("title"),
            arguments.GetOption("description"),
            arguments.GetOption("app-id"),
            HasOverwriteFlag(arguments)));

    private static WorkspaceAuthoringResult AddTest(
        WorkspaceAuthoringService service,
        CliArguments arguments)
        => service.AddTest(new WorkspaceTestCreateRequest(
            arguments.RequirePositional(3, "workspace path"),
            arguments.RequirePositional(4, "test identifier"),
            arguments.RequireOption("app-id"),
            arguments.GetOption("name"),
            arguments.GetOption("prompt"),
            arguments.GetOption("validation"),
            arguments.GetOptions("assertion"),
            arguments.GetOptions("required-secret"),
            HasOverwriteFlag(arguments)));

    private static WorkspaceAuthoringResult AddTrigger(
        WorkspaceAuthoringService service,
        CliArguments arguments)
        => service.AddTrigger(new WorkspaceTriggerCreateRequest(
            arguments.RequirePositional(3, "workspace path"),
            arguments.RequirePositional(4, "trigger identifier"),
            arguments.GetOption("event-kind") ?? "app.event",
            arguments.GetOption("app-id"),
            HasOverwriteFlag(arguments)));

    private static WorkspaceAuthoringResult AddSanitizer(
        WorkspaceAuthoringService service,
        CliArguments arguments)
        => service.AddSanitizer(new WorkspaceSanitizerCreateRequest(
            arguments.RequirePositional(3, "workspace path"),
            arguments.RequirePositional(4, "sanitizer identifier"),
            HasOverwriteFlag(arguments)));

    private static bool HasOverwriteFlag(CliArguments arguments)
        => arguments.HasFlag("force") || arguments.HasFlag("overwrite");

    private static string Render(
        WorkspaceAuthoringResult result,
        AppOperationResult? appRegistration = null,
        string? registrationNextStep = null)
    {
        var lines = new List<string> { result.Message };
        if (!string.IsNullOrWhiteSpace(result.DefinitionPath))
        {
            lines.Add($"Definition: {result.DefinitionPath}");
        }

        if (result.CreatedFiles.Count > 0)
        {
            lines.Add($"Created: {result.CreatedFiles.Count}");
        }

        if (result.UpdatedFiles.Count > 0)
        {
            lines.Add($"Updated: {result.UpdatedFiles.Count}");
        }

        if (result.ExistingFiles.Count > 0)
        {
            lines.Add($"Existing: {result.ExistingFiles.Count}");
        }

        if (appRegistration is not null)
        {
            lines.Add(appRegistration.Message);
            if (appRegistration.IsSuccess)
            {
                lines.Add("Automatic session analysis is enabled for this app.");
            }
        }
        else if (registrationNextStep is not null)
        {
            lines.Add("App registration skipped; automatic session analysis is not enabled for this workspace.");
            lines.Add($"Next: {registrationNextStep}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildHelp()
        => """
           Create and extend an Ansight workspace

           Usage:
             ansight workspace list
             ansight workspace init <workspace-path> [--app-id <id>] [--name <name>] [--no-register] [--force]
             ansight workspace add task <workspace-path> <id> [options]
             ansight workspace add test <workspace-path> <id> --app-id <id> [options]
             ansight workspace add trigger <workspace-path> <id> [options]
             ansight workspace add sanitizer <workspace-path> <id> [--force]

           `workspace list` shows app registrations that are linked to trusted codebases.
           Find every known app ID with `ansight app list`.

           Init registration:
             Interactive terminals prompt for an App ID and link it to this workspace so every
             finalized session is analyzed automatically. Leave the prompt blank to skip.
             --app-id <id>                   Register without prompting (recommended for CI)
             --name <name>                   Human-readable name for the registered app
             --no-register                   Skip registration without prompting

           What init creates:
             Every listed directory also gets an editable README.md explaining its role.
             ansight/package.json          Declares ES module semantics for plain .ts files
             ansight/tasks/                 TypeScript task modules for reusable workflows
               ansight-task.js              Named runtime permission constants
               ansight-task.d.ts            Task authoring API and type declarations
               tsconfig.json                TypeScript type-checking configuration
             ansight/tests/                 YAML test journeys used by test list/validate/run (JSON also supported)
             ansight/trends/           Observation spans, metrics, budgets, and regression policies
             ansight/sanitizers/            Typed PII/content functions for session export and sharing
               ansight-sanitizer.d.ts       Sanitizer inputs, tools, and return types
               tsconfig.json                TypeScript type-checking configuration
             ansight/triggers/              TypeScript event-driven automation modules
               ansight-trigger.d.ts         Trigger authoring API and type declarations
               tsconfig.json                TypeScript type-checking configuration
             ansight/schema/                Versioned test, trends, task, and trigger schemas

           What each component means:
             task
               A reusable operation an agent can invoke repeatedly by its exact task ID instead of
               rediscovering and re-planning the same tool sequence. This reduces execution time,
               tool calls, and token use. Its TypeScript uses typed Ansight/app APIs,
               accepts typed input, and records named assertions and structured output.
               Choose a task for a stable app-specific recipe such as "query every map annotation and
               assert that the expected areas exist".

             test
               A declarative YAML test journey. It identifies the app, tells the Ansight test
               runner what user journey to perform, and defines the observable final assertions.
               The all-in-one test command starts the simulator/emulator when needed, launches the
               installed app, waits for its Ansight session, and runs the scenario with Ansight tools.
               Choose a test for a user journey such as "open Point Perpendicular, open Seaside,
               and prove the details screen is correct".

             trigger
               A TypeScript handler that runs automatically when a connected resident host
               receives its matching app or session event. Host-side conditions can filter the event;
               the handler can validate it or return one app-tool action.
               Choose a trigger for reactive automation such as "capture map state whenever navigation
               settles" or "request diagnostics when an error log arrives".

           In short:
             task = let an agent cheaply reuse a known operation
             test = execute and validate an end-to-end user scenario
             trigger = react automatically to an app/session event

           Task options:
             --title <text>                  Human-readable task title
             --description <text>            Task purpose and expected result
             --app-id <id>                   Restrict the task to one application ID

           Test options:
             --app-id <id>                   Required application bundle/package ID
             --name <text>                   Human-readable test name
             --prompt <text>                 Scenario instructions for the test runner
             --validation <text>             Final-state validation instructions
             --assertion <text>              Observable assertion; may be repeated
             --required-secret <name>        Secret alias required by the test; may be repeated

           Trigger options:
             --event-kind <kind>             Event to handle; defaults to app.event
             --app-id <id>                   Restrict the trigger to one application ID

           Common options:
             --force                         Replace existing definitions or support files
             --json                          Emit versioned machine-readable output

           Initialization is idempotent. Existing customized support files are preserved unless
           --force is supplied. Definition IDs become filenames and may contain letters, numbers,
           dots, dashes, and underscores.
           """;
}
