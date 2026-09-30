using System.Globalization;
using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal static class ProcessSamplingCommands
{
    private const string ProcessPlatform = "process";

    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (arguments.ShowHelp || IsHelpAt(arguments, 2))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.RequirePositional(2, "process sampling action").ToLowerInvariant();
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
            "artifact" or "output" => ShowArtifact(lease.Runtime, arguments, output),
            _ => throw new CliUsageException(
                $"Unknown process sampling action '{action}'. "
                + "Run 'ansight profile sample help' for available commands.")
        };
    }

    private static async Task<int> ShowToolchainAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(3, "ansight profile sample tools");
        var toolchain = await runtime.NativeProfiling.GetProcessSampleToolchainAsync(cancellationToken)
            .ConfigureAwait(false);
        output.Write(
            new NativeProfilingToolchainOutput("ansight.process-sample-toolchain/v1", toolchain),
            () => $"Engine: {toolchain.Engine}\n"
                  + $"Capture tool: {toolchain.CaptureToolPath ?? "not found"}\n"
                  + toolchain.Message);
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
            "ansight profile sample start <pid> [--wait] [options]");
        var wait = arguments.HasFlag("wait");
        if (CliCommandContext.Current is null && !wait)
        {
            throw new CliHostUnavailableException(
                "A detached capture requires a resident host. Run 'ansight host run' first, or add --wait.");
        }

        var processIdText = arguments.RequirePositional(3, "process identifier");
        if (!int.TryParse(
                processIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0)
        {
            throw new CliUsageException("A positive process identifier is required.");
        }

        var request = new ProcessSampleRequest(
            processId,
            arguments.GetSecondsOption(
                "duration-seconds",
                TimeSpan.FromSeconds(3),
                maximumSeconds: 300),
            arguments.GetIntOption("interval-milliseconds", 1, 1, 1_000));
        var captureId = await runtime.NativeProfiling.StartProcessSampleAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!wait)
        {
            return WriteCapture("start", captureId, GetProcessCapture(runtime, captureId), runtime, output);
        }

        string? previousPhase = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = GetProcessCapture(runtime, captureId)
                           ?? throw new InvalidOperationException(
                               $"Capture '{captureId}' disappeared from the process sampling service.");
            if (!string.Equals(previousPhase, snapshot.Phase, StringComparison.Ordinal))
            {
                previousPhase = snapshot.Phase;
                output.WriteProgress($"[{snapshot.State}] {snapshot.Phase}");
            }

            if (snapshot.IsTerminal)
            {
                return WriteCapture("start", captureId, snapshot, runtime, output);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int ShowStatus(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile sample status <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        return WriteCapture(
            "status",
            captureId,
            GetProcessCapture(runtime, captureId),
            runtime,
            output);
    }

    private static int Cancel(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile sample cancel <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var cancelled = GetProcessCapture(runtime, captureId) is not null
                        && runtime.NativeProfiling.CancelCapture(captureId);
        output.Write(
            new NativeProfilingCancellationOutput(
                "ansight.process-sample-cancel/v1",
                captureId,
                cancelled),
            () => cancelled
                ? $"Cancellation requested for process sample '{captureId}'."
                : $"Process sample '{captureId}' is not active or was not found.");
        return cancelled ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static int ListCaptures(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight profile sample list [--limit <count>]");
        var limit = arguments.GetIntOption("limit", 100, 1, 1_000);
        var captures = runtime.NativeProfiling.ListCaptures()
            .Where(static capture => string.Equals(
                capture.Platform,
                ProcessPlatform,
                StringComparison.Ordinal))
            .OrderByDescending(static capture => capture.CreatedUtc)
            .Take(limit)
            .ToArray();
        output.Write(
            new ProcessSamplingCaptureListOutput("ansight.process-samples/v1", captures),
            () => captures.Length == 0
                ? "No process stack samples found."
                : string.Join(
                    Environment.NewLine,
                    captures.Select(capture =>
                        $"{capture.CaptureId}\t{capture.State}\t{capture.ProcessId}\t"
                        + $"{capture.ProcessName}\t{capture.CreatedUtc:O}")));
        return CliExitCodes.Success;
    }

    private static int ShowManifest(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile sample manifest <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var manifest = runtime.NativeProfiling.GetManifest(captureId);
        if (manifest is not null
            && !string.Equals(manifest.Platform, ProcessPlatform, StringComparison.Ordinal))
        {
            manifest = null;
        }

        var artifactPath = manifest is null
            ? null
            : runtime.NativeProfiling.GetProcessSampleArtifactPath(captureId);
        output.Write(
            new ProcessSamplingManifestOutput(
                "ansight.process-sample-manifest/v1",
                captureId,
                manifest is not null,
                manifest,
                artifactPath),
            () => manifest is null
                ? $"Process sample '{captureId}' was not found."
                : $"{manifest.CaptureId}\t{manifest.State}\tpid={manifest.ProcessId}\t"
                  + $"{manifest.ProcessName}\n"
                  + string.Join(
                      Environment.NewLine,
                      manifest.Artifacts.Select(artifact =>
                          $"{artifact.Kind}\t{artifact.Length}\t{artifact.RelativePath}"))
                  + (artifactPath is null ? string.Empty : $"\nArtifact: {artifactPath}"));
        return manifest is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int ShowArtifact(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output)
    {
        arguments.EnsurePositionalCount(4, "ansight profile sample artifact <capture-id>");
        var captureId = arguments.RequirePositional(3, "capture identifier");
        var artifactPath = GetProcessCapture(runtime, captureId) is null
            ? null
            : runtime.NativeProfiling.GetProcessSampleArtifactPath(captureId);
        output.Write(
            new ProcessSamplingArtifactOutput(
                "ansight.process-sample-artifact/v1",
                captureId,
                artifactPath is not null,
                artifactPath),
            () => artifactPath ?? $"Process sample artifact '{captureId}' was not found.");
        return artifactPath is null ? CliExitCodes.Failure : CliExitCodes.Success;
    }

    private static int WriteCapture(
        string operation,
        string captureId,
        NativeCaptureSnapshot? snapshot,
        RuntimeCoordinator runtime,
        CliOutput output)
    {
        var artifactPath = snapshot?.IsTerminal == true
            ? runtime.NativeProfiling.GetProcessSampleArtifactPath(captureId)
            : null;
        output.Write(
            new ProcessSamplingCaptureOutput(
                "ansight.process-sample/v1",
                operation,
                captureId,
                snapshot is not null,
                snapshot,
                artifactPath),
            () => snapshot is null
                ? $"Process sample '{captureId}' was not found."
                : $"{snapshot.CaptureId}\t{snapshot.State}\t{snapshot.Phase}"
                  + (string.IsNullOrWhiteSpace(snapshot.FailureMessage)
                      ? string.Empty
                      : $"\n{snapshot.FailureMessage}")
                  + (artifactPath is null ? string.Empty : $"\nArtifact: {artifactPath}"));
        return snapshot is null || snapshot.State is "failed" or "cancelled"
            ? CliExitCodes.Failure
            : CliExitCodes.Success;
    }

    private static NativeCaptureSnapshot? GetProcessCapture(
        RuntimeCoordinator runtime,
        string captureId)
    {
        var snapshot = runtime.NativeProfiling.GetCapture(captureId);
        return snapshot is not null
               && string.Equals(snapshot.Manifest.Platform, ProcessPlatform, StringComparison.Ordinal)
            ? snapshot
            : null;
    }

    private static bool IsHelpAt(CliArguments arguments, int position)
        => arguments.Positionals.Count > position
           && string.Equals(
               arguments.Positionals[position],
               "help",
               StringComparison.OrdinalIgnoreCase);

    private static string BuildHelp()
        => """
           Capture sampled thread stacks from a local macOS process (BETA)

           BETA FEATURE: Process stack sampling commands are experimental and may change before general availability.

           Usage:
             ansight profile sample tools
             ansight profile sample start <pid> [--wait] [options]
             ansight profile sample status <capture-id>
             ansight profile sample cancel <capture-id>
             ansight profile sample list [--limit <count>]
             ansight profile sample manifest <capture-id>
             ansight profile sample artifact <capture-id>

           Start options:
             --wait                            Keep this command attached until capture completion
             --duration-seconds <n>            Sample duration; default: 3, maximum: 300
             --interval-milliseconds <n>       Milliseconds between samples; default: 1, maximum: 1000

           This wraps /usr/bin/sample and attaches to an already-running local process. It supports
           macOS apps, Mac Catalyst apps, and iOS Simulator app processes. It does not attach to
           application processes running on physical iOS devices.

           The authoritative artifact is a plain-text sampled call graph. Use `artifact` to print
           its retained absolute path. Detached captures require `ansight host run`; add --wait
           when using a transient host.
           """;
}
