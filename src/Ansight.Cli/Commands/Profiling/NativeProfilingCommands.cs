using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal static class NativeProfilingCommands
{
    public static async Task<int> RunAsync(
        string platform,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (arguments.ShowHelp || IsHelpAt(arguments, 2))
        {
            return CliCommandHelp.Write(output, BuildHelp(platform));
        }

        var action = arguments.RequirePositional(2, "native profiling action").ToLowerInvariant();
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: false,
            cancellationToken).ConfigureAwait(false);
        return action switch
        {
            "tools" or "requirements" => await ShowToolchainAsync(
                platform,
                lease.Runtime,
                arguments,
                output,
                cancellationToken),
            "start" => await StartAsync(
                platform,
                lease.Runtime,
                arguments,
                output,
                cancellationToken),
            "status" or "get" => ShowStatus(platform, lease.Runtime, arguments, output),
            "cancel" => Cancel(platform, lease.Runtime, arguments, output),
            "list" => ListCaptures(platform, lease.Runtime, arguments, output),
            "manifest" => ShowManifest(platform, lease.Runtime, arguments, output),
            "artifact" or "trace" => ShowArtifact(platform, lease.Runtime, arguments, output),
            "overview" or "inspect" => await ShowOverviewAsync(
                platform,
                lease.Runtime,
                arguments,
                output,
                cancellationToken),
            "toc" when platform == "ios" => ShowInstrumentsToc(
                lease.Runtime,
                arguments,
                output),
            "query" or "sql" when platform == "android" => await QueryPerfettoAsync(
                lease.Runtime,
                arguments,
                output,
                cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown {platform} native profiling action '{action}'. "
                + $"Run 'ansight profile {platform} help' for available commands.")
        };
    }

    private static async Task<int> ShowToolchainAsync(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(3, $"ansight profile {platform} tools");
        var toolchain = await runtime.NativeProfiling.GetToolchainAsync(platform, cancellationToken)
            .ConfigureAwait(false);
        var inspectionToolchain = platform == "android"
            ? await runtime.NativeProfiling.GetPerfettoInspectionToolchainAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;
        output.Write(
            new NativeProfilingToolchainOutput(
                "ansight.native-toolchain/v1",
                toolchain,
                inspectionToolchain),
            () => $"Platform: {toolchain.Platform}\n"
                  + $"Engine: {toolchain.Engine}\n"
                  + $"Capture tool: {toolchain.CaptureToolPath ?? "not found"}\n"
                  + $"Version: {toolchain.CaptureToolVersion ?? "not available"}\n"
                  + (inspectionToolchain is null
                      ? string.Empty
                      : $"Trace Processor: {inspectionToolchain.ToolPath ?? "not found"}\n"
                        + $"Trace Processor version: {inspectionToolchain.Version ?? "not available"}\n")
                  + toolchain.Message);
        return toolchain.IsAvailable ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static async Task<int> StartAsync(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            $"ansight profile {platform} start <app-path> --app-id <id> --device-id <id> [--wait] [options]");
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

        var preset = ResolvePreset(platform, arguments.GetOption("preset"));
        var request = new NativeCaptureRequest(
            platform,
            applicationPath,
            arguments.RequireOption("app-id"),
            arguments.RequireOption("device-id"),
            preset,
            arguments.GetSecondsOption("duration-seconds", TimeSpan.FromSeconds(30), maximumSeconds: 300),
            Headless: arguments.HasFlag("headless"));
        var captureId = await runtime.NativeProfiling.StartCaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!wait)
        {
            return WriteCapture(
                platform,
                "start",
                captureId,
                runtime.NativeProfiling.GetCapture(captureId),
                output);
        }

        string? previousPhase = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = runtime.NativeProfiling.GetCapture(captureId)
                           ?? throw new InvalidOperationException(
                               $"Capture '{captureId}' disappeared from the native profiling service.");
            if (!string.Equals(previousPhase, snapshot.Phase, StringComparison.Ordinal))
            {
                previousPhase = snapshot.Phase;
                output.WriteProgress($"[{snapshot.State}] {snapshot.Phase}");
            }

            if (snapshot.IsTerminal)
            {
                return WriteCapture(platform, "start", captureId, snapshot, output);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int ShowStatus(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, $"ansight profile {platform} status <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        return WriteCapture(
            platform,
            "status",
            captureId,
            GetPlatformCapture(platform, runtime, captureId),
            output);
    }

    private static int Cancel(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, $"ansight profile {platform} cancel <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var cancelled = GetPlatformCapture(platform, runtime, captureId) is not null
                        && runtime.NativeProfiling.CancelCapture(captureId);
        output.Write(
            new NativeProfilingCancellationOutput(
                "ansight.native-capture-cancel/v1",
                captureId,
                cancelled),
            () => cancelled
                ? $"Cancellation requested for capture '{captureId}'."
                : $"Capture '{captureId}' is not an active {platform} native capture.");
        return cancelled ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static int ListCaptures(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, $"ansight profile {platform} list [--limit <count>]");
        var limit = arguments.GetIntOption("limit", 100, 1, 1_000);
        var captures = runtime.NativeProfiling.ListCaptures()
            .Where(capture => string.Equals(capture.Platform, platform, StringComparison.Ordinal))
            .OrderByDescending(static capture => capture.CreatedUtc)
            .Take(limit)
            .ToArray();
        output.Write(
            new NativeProfilingCaptureListOutput(
                "ansight.native-captures/v1",
                platform,
                captures),
            () => captures.Length == 0
                ? $"No {platform} native captures found."
                : string.Join(
                    Environment.NewLine,
                    captures.Select(capture =>
                        $"{capture.CaptureId}\t{capture.State}\t{capture.Preset}\t{capture.ApplicationPath}\t{capture.CreatedUtc:O}")));
        return CliExitCodes.Success;
    }

    private static int ShowManifest(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, $"ansight profile {platform} manifest <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var manifest = runtime.NativeProfiling.GetManifest(captureId);
        if (manifest is not null
            && !string.Equals(manifest.Platform, platform, StringComparison.Ordinal))
        {
            manifest = null;
        }

        output.Write(
            new NativeProfilingManifestOutput(
                "ansight.native-capture-manifest/v1",
                captureId,
                manifest is not null,
                manifest),
            () => manifest is null
                ? $"{platform} native capture '{captureId}' was not found."
                : $"{manifest.CaptureId}\t{manifest.State}\t{manifest.Engine}\t{manifest.Preset}\n"
                  + string.Join(
                      Environment.NewLine,
                      manifest.Artifacts.Select(artifact =>
                          $"{artifact.Kind}\t{artifact.Length}\t{artifact.RelativePath}")));
        return manifest is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int ShowArtifact(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, $"ansight profile {platform} artifact <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var artifact = runtime.NativeProfiling.GetArtifact(captureId);
        if (artifact is not null
            && !string.Equals(artifact.Platform, platform, StringComparison.Ordinal))
        {
            artifact = null;
        }

        output.Write(
            new NativeProfilingArtifactOutput(
                "ansight.native-profile-artifact/v1",
                captureId,
                artifact is not null,
                artifact),
            () => artifact is null
                ? $"{platform} native capture '{captureId}' or its authoritative artifact was not found."
                : artifact.Path);
        return artifact is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static async Task<int> ShowOverviewAsync(
        string platform,
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(4, $"ansight profile {platform} overview <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var manifest = runtime.NativeProfiling.GetManifest(captureId);
        NativeProfileCaptureInspection? inspection = null;
        if (manifest is not null
            && string.Equals(manifest.Platform, platform, StringComparison.Ordinal))
        {
            inspection = await runtime.NativeProfiling.InspectCaptureAsync(
                captureId,
                cancellationToken).ConfigureAwait(false);
        }

        output.Write(
            new NativeProfilingInspectionOutput(
                "ansight.native-profile-inspection/v1",
                captureId,
                inspection is not null,
                inspection),
            () => inspection is null
                ? $"{platform} native capture '{captureId}' was not found."
                : FormatInspection(inspection));
        return inspection is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int ShowInstrumentsToc(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile ios toc <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var manifest = runtime.NativeProfiling.GetManifest(captureId);
        var toc = manifest is not null
                  && string.Equals(manifest.Platform, "ios", StringComparison.Ordinal)
            ? runtime.NativeProfiling.InspectInstrumentsToc(captureId)
            : null;
        output.Write(
            new InstrumentsTocInspectionOutput(
                "ansight.instruments-toc-inspection/v1",
                captureId,
                toc is not null,
                toc),
            () => toc is null
                ? $"iOS native capture '{captureId}' was not found."
                : $"TOC: {toc.Path}\nRuns: {toc.RunCount}\nTables: {toc.TableCount}\n"
                  + string.Join(
                      Environment.NewLine,
                      toc.Schemas.Select(schema => $"{schema.Schema}\t{schema.Occurrences}")));
        return toc is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static async Task<int> QueryPerfettoAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            4,
            "ansight profile android query <capture-id> --sql <read-only-query> [--limit <rows>]");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var maximumRows = arguments.GetIntOption("limit", 1_000, 1, 10_000);
        var query = await runtime.NativeProfiling.QueryPerfettoAsync(
            captureId,
            arguments.RequireOption("sql"),
            maximumRows,
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new PerfettoQueryInspectionOutput(
                "ansight.perfetto-query-inspection/v1",
                captureId,
                query),
            () => FormatPerfettoQuery(query));
        return CliExitCodes.Success;
    }

    private static string FormatInspection(NativeProfileCaptureInspection inspection)
    {
        var lines = new List<string>
        {
            $"Capture: {inspection.CaptureId}",
            $"Platform: {inspection.Platform}",
            $"Engine: {inspection.Engine}",
            $"Preset: {inspection.Preset}",
            $"State: {inspection.State}",
            $"Requested duration: {inspection.RequestedDurationSeconds}s",
            $"Artifact: {inspection.AuthoritativeArtifact?.Path ?? "not found"}"
        };
        if (inspection.CaptureElapsedSeconds is not null)
        {
            lines.Add($"Capture job elapsed: {inspection.CaptureElapsedSeconds:F3}s");
        }

        if (inspection.Instruments is not null)
        {
            lines.Add($"Instruments runs: {inspection.Instruments.RunCount}");
            lines.Add($"Instruments tables: {inspection.Instruments.TableCount}");
            lines.Add($"Processes: {inspection.Instruments.Processes.Count}");
        }

        if (inspection.Perfetto is not null)
        {
            lines.Add($"Perfetto data sources: {string.Join(", ", inspection.Perfetto.DataSources)}");
            lines.Add($"Trace Processor: {inspection.Perfetto.Toolchain.ToolPath ?? "not found"}");
            if (inspection.Perfetto.TraceOverview is not null)
            {
                lines.Add(FormatPerfettoQuery(inspection.Perfetto.TraceOverview));
            }
        }

        if (inspection.Warnings.Count > 0)
        {
            lines.AddRange(inspection.Warnings.Select(warning => $"Warning: {warning}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatPerfettoQuery(PerfettoQueryInspection query)
    {
        var lines = new List<string>();
        if (query.Columns.Count > 0)
        {
            lines.Add(string.Join('\t', query.Columns));
        }

        lines.AddRange(query.Rows.Select(row => string.Join(
            '\t',
            row.Select(static value => value ?? "NULL"))));
        if (query.WasTruncated)
        {
            lines.Add($"Results truncated after {query.RowCount} rows.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int WriteCapture(
        string platform,
        string operation,
        string captureId,
        NativeCaptureSnapshot? snapshot,
        CliOutput output)
    {
        if (snapshot is not null
            && !string.Equals(snapshot.Manifest.Platform, platform, StringComparison.Ordinal))
        {
            snapshot = null;
        }

        output.Write(
            new NativeProfilingCaptureOutput(
                "ansight.native-capture/v1",
                operation,
                captureId,
                snapshot is not null,
                snapshot),
            () => snapshot is null
                ? $"{platform} native capture '{captureId}' was not found."
                : $"{snapshot.CaptureId}\t{snapshot.State}\t{snapshot.Phase}"
                  + (string.IsNullOrWhiteSpace(snapshot.FailureMessage)
                      ? string.Empty
                      : $"\n{snapshot.FailureMessage}"));
        return snapshot is null || snapshot.State is "failed" or "cancelled"
            ? CliExitCodes.Failure
            : CliExitCodes.Success;
    }

    private static NativeCaptureSnapshot? GetPlatformCapture(
        string platform,
        RuntimeCoordinator runtime,
        string captureId)
    {
        var snapshot = runtime.NativeProfiling.GetCapture(captureId);
        return snapshot is not null
               && string.Equals(snapshot.Manifest.Platform, platform, StringComparison.Ordinal)
            ? snapshot
            : null;
    }

    private static string ResolvePreset(string platform, string? requestedPreset)
    {
        var preset = string.IsNullOrWhiteSpace(requestedPreset)
            ? platform == "ios" ? "launch" : "system"
            : requestedPreset.Trim().ToLowerInvariant();
        var isSupported = platform switch
        {
            "ios" => preset is "launch" or "cpu" or "memory" or "leaks",
            "android" => preset is "launch" or "system" or "memory" or "native-heap" or "managed-heap",
            _ => false
        };
        return isSupported
            ? preset
            : throw new CliUsageException(
                platform == "ios"
                    ? "iOS native preset must be launch, cpu, memory, or leaks."
                    : "Android native preset must be launch, system, memory, native-heap, or managed-heap.");
    }

    private static bool IsHelpAt(CliArguments arguments, int position)
        => arguments.Positionals.Count > position
           && string.Equals(
               arguments.Positionals[position],
               "help",
               StringComparison.OrdinalIgnoreCase);

    private static string BuildHelp(string platform)
        => platform == "ios"
            ? """
              Capture and manage iOS Instruments profiles (BETA)

              BETA FEATURE: Native iOS profiling commands are experimental and may change before general availability.

              Usage:
                ansight profile ios tools
                ansight profile ios start <app-path> --app-id <id> --device-id <id> [--wait] [options]
                ansight profile ios status <capture-id>
                ansight profile ios cancel <capture-id>
                ansight profile ios list [--limit <count>]
                ansight profile ios manifest <capture-id>
                ansight profile ios artifact <capture-id>
                ansight profile ios overview <capture-id>
                ansight profile ios toc <capture-id>

              Presets:
                launch      Instruments App Launch template; default
                cpu         Instruments Time Profiler template
                memory      Instruments Allocations template
                leaks       Instruments Leaks template

              Start options:
                --headless                Do not open the Simulator window; shown by default
                --wait                     Keep this command attached until capture completion
                --preset <name>            launch, cpu, memory, or leaks; default: launch
                --duration-seconds <n>     Capture duration; default: 30, maximum: 300
                --app-id <id>              Application bundle identifier; required
                --device-id <id>           Simulator or physical device UDID; required

              Inspection:
                artifact     Resolve the authoritative .trace bundle path
                overview     Summarize capture provenance, artifacts, runs, tables, and processes
                toc          Inventory schemas and processes from the retained xctrace table of contents

              The authoritative artifact is an Instruments .trace bundle. Ansight validates it by exporting its table of contents.
              """
            : """
              Capture and manage Android Perfetto profiles (BETA)

              BETA FEATURE: Native Android profiling commands are experimental and may change before general availability.

              Usage:
                ansight profile android tools
                ansight profile android start <app-path> --app-id <id> --device-id <id> [--wait] [options]
                ansight profile android status <capture-id>
                ansight profile android cancel <capture-id>
                ansight profile android list [--limit <count>]
                ansight profile android manifest <capture-id>
                ansight profile android artifact <capture-id>
                ansight profile android overview <capture-id>
                ansight profile android query <capture-id> --sql <query> [--limit <rows>]

              Presets:
                system      Perfetto system trace around a cold application launch; default
                launch      Perfetto startup-focused system trace
                memory      Process and system memory counters around a cold launch
                native-heap Sampled native allocations and call stacks; Android 10+
                managed-heap ART Java/Kotlin retention graph; Android 11+

              Start options:
                --headless                  Start emulators without a window; shown by default
                --wait                       Keep this command attached until capture completion
                --preset <name>              system, launch, memory, native-heap, or managed-heap
                --duration-seconds <n>       Capture duration; default: 30, maximum: 300
                --app-id <id>                Android package identifier; required
                --device-id <id>             Emulator or physical device serial; required

              Inspection:
                artifact     Resolve the authoritative .perfetto-trace path
                overview     Summarize provenance, data sources, artifacts, and core trace counts
                query        Run one bounded read-only SELECT/WITH query through local Trace Processor

              Query options:
                --sql <query>               One read-only SELECT or WITH statement; required
                --limit <rows>              Maximum returned rows; default: 1000, maximum: 10000

              Set ANSIGHT_TRACE_PROCESSOR_PATH when trace_processor or trace_processor_shell is not on PATH.
              """;
}
