using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal static class ProfilingCommands
{
    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (IsTopLevelHelpRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var technology = arguments.RequirePositional(1, "profiling technology").ToLowerInvariant();
        return technology switch
        {
            "dotnet" or ".net" or "net" => await RunDotNetAsync(
                arguments,
                output,
                cancellationToken),
            "ios" => await NativeProfilingCommands.RunAsync(
                "ios",
                arguments,
                output,
                cancellationToken),
            "android" => await NativeProfilingCommands.RunAsync(
                "android",
                arguments,
                output,
                cancellationToken),
            "sample" or "process" or "stacks" => await ProcessSamplingCommands.RunAsync(
                arguments,
                output,
                cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown profiling technology '{technology}'. Available technologies: ios, android, dotnet, sample. "
                + "Run 'ansight profile help' for available commands.")
        };
    }

    private static async Task<int> RunDotNetAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (arguments.ShowHelp || IsHelpAt(arguments, 2))
        {
            return CliCommandHelp.Write(output, BuildDotNetHelp());
        }

        var action = arguments.RequirePositional(2, ".NET profiling action").ToLowerInvariant();
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        return action switch
        {
            "tools" or "requirements" => await ShowToolchainAsync(
                lease.Runtime,
                arguments,
                output,
                cancellationToken),
            "start" => await StartAsync(lease.Runtime, arguments, output, cancellationToken),
            "status" or "get" => ShowStatus(lease.Runtime, arguments, output),
            "cancel" => Cancel(lease.Runtime, arguments, output),
            "list" => ListCaptures(lease.Runtime, arguments, output),
            "manifest" => ShowManifest(lease.Runtime, arguments, output),
            "import" => await ImportAsync(lease.Runtime, arguments, output, cancellationToken),
            "speedscope" or "export-speedscope" => ShowSpeedScope(
                lease.Runtime,
                arguments,
                output),
            "overview" or "startup" or "startup-timeline" or "timeline"
                or "cpu" or "hotspots" or "cpu-hotspots"
                or "call-tree" or "calltree"
                or "threads" or "thread-activity"
                or "gc" or "gc-summary"
                or "jit" or "jit-summary"
                or "exceptions" => ProfilingAnalysisCommands.Run(
                    lease.Runtime,
                    arguments,
                    output,
                    action),
            _ => throw new CliUsageException(
                $"Unknown .NET profiling action '{action}'. "
                + "Run 'ansight profile dotnet help' for available commands.")
        };
    }

    private static async Task<int> ShowToolchainAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(3, "ansight profile dotnet tools");
        var toolchain = await runtime.Profiling.GetToolchainAsync(cancellationToken).ConfigureAwait(false);
        output.Write(
            new ProfilingToolchainOutput("ansight.dotnet-toolchain/v1", toolchain),
            () => $"dotnet-trace: {toolchain.DotNetTracePath ?? "not found"}\n"
                  + $"dotnet-dsrouter: {toolchain.DotNetDsRouterPath ?? "not found"}\n"
                  + $"ADB: {toolchain.AdbPath ?? "not found"}\n"
                  + $"Xcode: {toolchain.XcodeVersion ?? "not available"}");
        return toolchain.IsAvailable ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static async Task<int> StartAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight profile dotnet start <app-path> --app-id <id> --device-id <id> [--wait] [options]");
        var wait = arguments.HasFlag("wait");
        if (CliCommandContext.Current is null && !wait)
        {
            throw new CliHostUnavailableException(
                "A detached capture requires a resident host. Run 'ansight host run' first, or add --wait.");
        }

        var applicationPath = Path.GetFullPath(arguments.RequirePositional(3, "application path"));
        if (!File.Exists(applicationPath) && !Directory.Exists(applicationPath))
        {
            throw new CliUsageException($"Application artifact '{applicationPath}' was not found.");
        }

        var request = new DotNetCaptureRequest(
            applicationPath,
            arguments.RequireOption("app-id"),
            arguments.RequireOption("device-id"),
            arguments.GetSecondsOption("duration-seconds", TimeSpan.FromSeconds(30), maximumSeconds: 300),
            arguments.GetOption("symbols"),
            Headless: arguments.HasFlag("headless"));
        var captureId = await runtime.Profiling.StartCaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!wait)
        {
            var snapshot = runtime.Profiling.GetCapture(captureId);
            return WriteCapture("start", captureId, snapshot, output);
        }

        string? previousPhase = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = runtime.Profiling.GetCapture(captureId)
                           ?? throw new InvalidOperationException(
                               $"Capture '{captureId}' disappeared from the profiling service.");
            if (!string.Equals(previousPhase, snapshot.Phase, StringComparison.Ordinal))
            {
                previousPhase = snapshot.Phase;
                output.WriteProgress($"[{snapshot.State}] {snapshot.Phase}");
            }

            if (snapshot.IsTerminal)
            {
                return WriteCapture("start", captureId, snapshot, output);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int ShowStatus(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile dotnet status <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        return WriteCapture("status", captureId, runtime.Profiling.GetCapture(captureId), output);
    }

    private static int Cancel(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile dotnet cancel <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var cancelled = runtime.Profiling.CancelCapture(captureId);
        output.Write(
            new ProfilingCancellationOutput(
                "ansight.dotnet-capture-cancel/v1",
                captureId,
                cancelled),
            () => cancelled
                ? $"Cancellation requested for capture '{captureId}'."
                : $"Capture '{captureId}' is not active or was not found.");
        return cancelled ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static int ListCaptures(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight profile dotnet list [--limit <count>]");
        var limit = arguments.GetIntOption("limit", 100, 1, 1_000);
        var captures = runtime.Profiling.ListCaptures()
            .OrderByDescending(static capture => capture.CreatedUtc)
            .Take(limit)
            .ToArray();
        output.Write(
            new ProfilingCaptureListOutput("ansight.dotnet-captures/v1", captures),
            () => captures.Length == 0
                ? "No .NET trace captures found."
                : string.Join(
                    Environment.NewLine,
                    captures.Select(capture =>
                        $"{capture.CaptureId}\t{capture.State}\t{capture.ApplicationPath}\t{capture.CreatedUtc:O}")));
        return CliExitCodes.Success;
    }

    private static async Task<int> ImportAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(4, "ansight profile dotnet import <trace.nettrace> [options]");
        var tracePath = Path.GetFullPath(arguments.RequirePositional(3, "NetTrace path"));
        var manifest = await runtime.Profiling.ImportTraceAsync(
            tracePath,
            arguments.GetOption("app-id"),
            arguments.GetOption("application-path"),
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new ProfilingImportOutput("ansight.dotnet-capture-import/v1", manifest),
            () => $"Imported capture '{manifest.CaptureId}' with {manifest.Artifacts.Count} artifact(s).");
        return CliExitCodes.Success;
    }

    private static int ShowManifest(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile dotnet manifest <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var manifest = runtime.Profiling.GetManifest(captureId);
        output.Write(
            new ProfilingManifestOutput(
                "ansight.dotnet-capture-manifest/v1",
                captureId,
                manifest is not null,
                manifest),
            () => manifest is null
                ? $"Capture '{captureId}' was not found."
                : $"{manifest.CaptureId}\t{manifest.State}\t{manifest.ApplicationPath}\n"
                  + string.Join(
                      Environment.NewLine,
                      manifest.Artifacts.Select(artifact =>
                          $"{artifact.Kind}\t{artifact.Length}\t{artifact.RelativePath}")));
        return manifest is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int ShowSpeedScope(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile dotnet speedscope <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var speedScope = runtime.Profiling.GetSpeedScopeArtifact(captureId);
        output.Write(
            new ProfilingSpeedScopeOutput(
                "ansight.dotnet-speedscope/v1",
                captureId,
                speedScope is not null,
                speedScope),
            () => speedScope is null
                ? $"Capture '{captureId}' does not contain a SpeedScope derivative."
                : speedScope.FilePath);
        return speedScope is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int WriteCapture(
        string operation,
        string captureId,
        DotNetCaptureSnapshot? snapshot,
        CliOutput output)
    {
        output.Write(
            new ProfilingCaptureOutput(
                "ansight.dotnet-capture/v1",
                operation,
                captureId,
                snapshot is not null,
                snapshot),
            () => snapshot is null
                ? $"Capture '{captureId}' was not found."
                : $"{snapshot.CaptureId}\t{snapshot.State}\t{snapshot.Phase}"
                  + (string.IsNullOrWhiteSpace(snapshot.FailureMessage)
                      ? string.Empty
                      : $"\n{snapshot.FailureMessage}"));
        if (snapshot is null || snapshot.State is "failed" or "cancelled")
        {
            return CliExitCodes.Failure;
        }

        return CliExitCodes.Success;
    }

    private static bool IsTopLevelHelpRequested(CliArguments arguments)
        => (arguments.ShowHelp && arguments.Positionals.Count == 1)
           || IsHelpAt(arguments, 1);

    private static bool IsHelpAt(CliArguments arguments, int position)
        => arguments.Positionals.Count > position
           && string.Equals(
               arguments.Positionals[position],
               "help",
               StringComparison.OrdinalIgnoreCase);

    private static string BuildHelp()
        => """
           Profile applications through technology-specific command groups (BETA)

           BETA FEATURE: Profiling commands are experimental and may change before general availability.

           Usage:
             ansight profile <technology> <command> [arguments] [options]
             ansight profile <technology> --help

           Technologies:
             ios         Capture native Instruments profiles
             android     Capture native Perfetto system traces
             dotnet      Capture, manage, and analyze .NET EventPipe traces
             sample      Sample thread stacks from a local macOS process

           Run `ansight profile dotnet --help`, `ansight profile ios --help`,
           `ansight profile android --help`, or `ansight profile sample --help` for a complete
           command reference.
           """;

    private static string BuildDotNetHelp()
        => """
           Capture, manage, and analyze .NET EventPipe traces (BETA)

           BETA FEATURE: .NET profiling commands are experimental and may change before general availability.

           Usage:
             ansight profile dotnet tools
             ansight profile dotnet start <app-path> --app-id <id> --device-id <id> [--wait] [options]
             ansight profile dotnet status <capture-id>
             ansight profile dotnet cancel <capture-id>
             ansight profile dotnet list [--limit <count>]
             ansight profile dotnet manifest <capture-id>
             ansight profile dotnet import <trace.nettrace> [options]
             ansight profile dotnet speedscope <capture-id>
             ansight profile dotnet overview <capture-id> [selection options]
             ansight profile dotnet startup <capture-id> [selection options]
             ansight profile dotnet cpu <capture-id> [--limit <count>] [selection options]
             ansight profile dotnet call-tree <capture-id> [options]
             ansight profile dotnet threads <capture-id> [selection options]
             ansight profile dotnet gc <capture-id> [--limit <count>] [selection options]
             ansight profile dotnet jit <capture-id> [--limit <count>] [selection options]
             ansight profile dotnet exceptions <capture-id> [--limit <count>] [selection options]

           .NET commands:
             tools       Report tracing, ADB, and Xcode requirements; alias: requirements
             start       Install and profile a supplied .app, .apk, or .ipa artifact
             status      Show capture state and phase; alias: get
             cancel      Cancel an active capture
             list        List recent captures
             manifest    Show the immutable capture manifest and artifact inventory
             import      Import an existing .nettrace into Ansight capture storage
             speedscope  Resolve the derived SpeedScope profile path; alias: export-speedscope
             overview    Summarize events, CPU samples, GC, JIT, exceptions, and symbols
             startup     Show trace, JIT, GC, exception, and application-ready milestones
             cpu         Rank methods by exclusive and inclusive sampled CPU activity
             call-tree   Show a bounded sampled root-to-leaf call tree
             threads     Rank processes and threads by sampled CPU activity
             gc          Summarize garbage collections and longest intervals
             jit         Summarize JIT activity and rank methods by IL size
             exceptions  Group thrown exceptions by type and message

           Start options:
             --headless                Do not open simulator/emulator windows; shown by default
             --wait                     Keep this command attached until capture completion
             --duration-seconds <n>     Capture duration; default: 30, maximum: 300
             --app-id <id>              Bundle or package identifier to launch; required
             --device-id <id>           Target simulator, emulator, or physical device; required
             --symbols <path>           Optional portable PDB file or directory

           Import options:
             --app-id <id>              Associate the imported trace with an app
             --application-path <path>  Associate the trace with an application artifact

           Analysis selection options:
             --start-ms <n>             Inclusive start offset from trace start
             --end-ms <n>               Inclusive end offset from trace start
             --process-id <id>          Restrict analysis to one process
             --thread-id <id>           Restrict analysis to one managed or OS thread

           Analysis result options:
             --limit <count>            CPU maximum 500; GC/JIT/exceptions maximum 1000
             --max-depth <count>        Call-tree depth; default 32, maximum 128
             --max-children <count>     Children per call-tree node; default 50, maximum 200

           Ansight never builds the application. The supplied artifact must have EnableDiagnostics=true.
           DiagnosticConfiguration: Android emulator 10.0.2.2:9000,suspend,connect;
           Android device 127.0.0.1:9000,suspend,connect; iOS 127.0.0.1:9000,suspend,listen.
           For an exact ready boundary, emit StartupComplete from EventSource Ansight-DotNet-Startup.
           Detached captures require `ansight host run`;
           add --wait when using a transient host. Find capture IDs with `ansight profile dotnet
           list`, device IDs with `ansight device list`, and app IDs with `ansight app list`.
           """;

}
