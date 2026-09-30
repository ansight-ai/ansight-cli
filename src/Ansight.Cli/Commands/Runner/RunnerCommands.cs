using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.Cli.Commands.Secret;

namespace Ansight.Cli.Commands.Runner;

internal static class RunnerCommands
{
    public static Task<int> RunAsync(CliArguments arguments, CliOutput output, CancellationToken cancellationToken, TextReader? standardInput = null, bool standardInputIsInteractive = false)
    {
        if (CliCommandHelp.IsRequested(arguments) || arguments.Positionals.Count == 1)
            return Task.FromResult(CliCommandHelp.Write(output, Help));

        return Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("RunnerCommands.RunAsync", [arguments, output, cancellationToken, standardInput, standardInputIsInteractive]);
    }

    internal static string FormatRegistration(string displayName, Guid machineId)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<string>("RunnerCommands.FormatRegistration", [displayName, machineId]);

    internal static Task<int> StartForHostAsync(CliArguments arguments, CliOutput output, CliRuntimeOptions options, RuntimeCoordinator runtime, CancellationToken cancellationToken)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("RunnerCommands.StartForHostAsync", [arguments, output, options, runtime, cancellationToken]);

    internal static void EnsureRegistered(CliRuntimeOptions options)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<object?>("RunnerCommands.EnsureRegistered", [options]);

    internal static string FormatMachines(JsonElement machines)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<string>("RunnerCommands.FormatMachines", [machines]);


    private const string Help = """
        Register and operate an outbound-only remote execution runner

        Usage:
          ansight runner setup <app.app|app.apk> [workspace] [options]
          ansight runner register --team-id <uuid> [options]
          ansight runner start [--once] [--poll-seconds <n>] [--skip-build-preflight]
          ansight runner refresh
          ansight runner status
          ansight runner revoke
          ansight runner machines [--team-id <uuid>]
          ansight runner list [--team-id <uuid>]
          ansight runner jobs --team-id <uuid> [--limit <n>]
          ansight runner submit --team-id <uuid> --kind build|test|app-execute [options]
          ansight runner secrets list --team-id <uuid> --app-id <id>
          ansight runner secrets set <alias> --team-id <uuid> --app-id <id> [--stdin|--from-env <name>]
          ansight runner secrets remove <alias> --team-id <uuid> --app-id <id>
          ansight runner cancel <job-id> [--team-id <uuid>]

        Registration options:
          --team-id <uuid>              Organisation to register with; setup can prompt
          --build <path>                Build path alternative to the positional argument
          --app <path>                  Backward-compatible alias for --build
          --workspace <path>            Repository root alternative to the positional argument
          --app-id <id>                 Override the identifier detected from the build
          --app-name <name>             App display name; defaults to the bundle name
          --name <name>                 Runner display name
          --kind <kind>                 Allowed typed job; repeatable
          --max-concurrency <n>         Jobs this runner can run on separate devices at once (1-16; default 4)
          --yes                         Accept the setup summary without confirmation
          --wait-seconds <n>            Upload availability wait; default 300
          --skip-preflight              Skip setup's local build preflight (runner preflight remains enabled)
          --execution-mode sdk|device   Setup preflight mode; choose device for SDK-less virtual builds
          --skip-upload                 Register app/machine without uploading build or workspace

        Worker options:
          --skip-build-preflight        Skip the repeated post-sync package check; hash and size checks still apply

        Worker lifecycle:
          Registration saves this machine's identity and capabilities, but does not
          start job processing. Use `ansight host run --runner` to run the resident
          host and remote worker together. `ansight runner start` remains available
          for worker-only and CI environments.

        Submission options:
          --runner-id <uuid>            Target one runner; otherwise any compatible runner
          --app-id <id>                 Registered application ID
          --build-id <uuid>             Uploaded cloud build artifact; omit to use latest for platform
          --platform ios|android        Required for latest build selection; repeat to queue both platforms
          --task-id <id>                Repository task; repeat to queue separate jobs
          --workspace-id <uuid>         Uploaded cloud workspace artifact (build/test; build requires --full-workspace upload)
          --test-id <id>                Workspace test ID; repeat to queue separate jobs
          --secret <alias>             Grant a team secret alias to each job; repeatable
          --secret-env <alias>=<name>  Include a one-run secret from an environment variable; repeatable
          --prompt <text>               Bounded app-execute goal
          --prompt-file <path>          Read the app-execute goal from a UTF-8 file
          --device-id <id>              Exact simulator/emulator; repeat to target multiple devices
          --[no-]headless               Hide or show the simulator/emulator; default headless
          --execution-mode sdk|device   SDK connection (default) or SDK-less virtual device UI
          --requires-tool <name>        Required reported tool; repeatable
          --[no-]upload-recordings      Upload completed session recordings; default on for test/app-execute
          --[no-]upload-trends          Upload Trends for this job's sessions; default on for test/app-execute
          --[no-]upload-test-results    Upload test-run results; default on for test jobs
          --timeout-seconds <n>         30-21600; default 3600
          --idempotency-key <value>     Deduplicate a submitter's request

        Jobs are durable and leased. App executions are never automatically retried
        after an uncertain lease expiry. Remote app execution closes the launched app
        before uploading its completed session; the simulator remains running.
        Repeating --device-id for app execution starts the same goal on those devices
        concurrently. Use --runner-id to bind all selected devices to one machine.
        Repeating --test-id, --task-id, or --platform queues separate durable jobs
        that compatible runners can claim concurrently (up to 32 jobs per submit).
        Register with --max-concurrency above 1 to run jobs on distinct devices
        at the same time on this runner.
        The machine credential remains in encrypted
        local storage and is never passed to the typed child execution.

        Account operations require an organisation admin or owner. API-key callers
        need runners:read for machines/jobs and runners:write for submit/cancel;
        set ANSIGHT_RUNNER_API_KEY and ANSIGHT_TEAM_ID to use those grants.

        Discover organisations available to the signed-in account with
        `ansight cloud team list`, then select one with `--team-id <uuid>`.

        `runner setup` uses the signed-in admin/owner account to register the app,
        initialize and upload the workspace, upload the iOS Simulator or Android build, and
        register this machine. Its temporary upload key is revoked automatically.

        Once registered, `ansight runner machines` uses the local organisation automatically.
        Machines are also visible in the web portal under Account > Runners.
        """;

}
