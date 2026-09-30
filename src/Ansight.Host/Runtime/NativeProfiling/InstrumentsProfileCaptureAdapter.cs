namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed class InstrumentsProfileCaptureAdapter : INativeProfileCaptureAdapter
{
    internal const string TraceArtifactKind = "apple-instruments-trace";
    internal const string TableOfContentsArtifactKind = "apple-instruments-toc";
    private const string XcrunPath = "/usr/bin/xcrun";
    private readonly INativeProfilingProcessRunner processRunner;

    public InstrumentsProfileCaptureAdapter(INativeProfilingProcessRunner processRunner)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public string Platform => NativeProfilePlatforms.Ios;

    public string Engine => "instruments";

    public bool SupportsPreset(string preset)
        => preset is NativeProfilePresets.Launch
            or NativeProfilePresets.Cpu
            or NativeProfilePresets.Memory
            or NativeProfilePresets.Leaks;

    public async Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
    {
        if ((!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
            || !File.Exists(XcrunPath))
        {
            return new NativeProfileToolchain(
                Platform,
                Engine,
                false,
                null,
                null,
                null,
                "iOS native profiling requires Xcode on macOS.");
        }

        var findResult = await RunToolAsync(
            ["--find", "xctrace"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (!findResult.IsSuccess || string.IsNullOrWhiteSpace(findResult.StandardOutput))
        {
            return new NativeProfileToolchain(
                Platform,
                Engine,
                false,
                XcrunPath,
                null,
                null,
                "Xcode is installed, but xctrace could not be resolved.");
        }

        var versionResult = await RunToolAsync(
            ["xctrace", "version"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        var xcodeResult = await RunToolAsync(
            ["xcodebuild", "-version"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return new NativeProfileToolchain(
            Platform,
            Engine,
            versionResult.IsSuccess,
            findResult.StandardOutput.Trim(),
            ReadOutput(versionResult),
            ReadOutput(xcodeResult),
            versionResult.IsSuccess
                ? "Instruments native capture is available."
                : "xctrace was found, but its version could not be read.");
    }

    public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        NativeProfileAdapterContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var template = context.Preset switch
        {
            NativeProfilePresets.Launch => "App Launch",
            NativeProfilePresets.Cpu => "Time Profiler",
            NativeProfilePresets.Memory => "Allocations",
            NativeProfilePresets.Leaks => "Leaks",
            _ => throw new InvalidOperationException(
                $"Instruments does not support native preset '{context.Preset}'.")
        };
        var tracePath = Path.Combine(context.CapturePath, "raw", "capture.trace");
        var tocPath = Path.Combine(context.CapturePath, "derived", "instruments-toc.xml");
        context.Log($"Starting Instruments template '{template}' on '{context.DeviceId}'.");
        var captureResult = await processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                context.CaptureToolPath,
                [
                    "record",
                    "--template",
                    template,
                    "--device",
                    context.DeviceId,
                    "--time-limit",
                    $"{Math.Ceiling(context.Duration.TotalSeconds):0}s",
                    "--output",
                    tracePath,
                    "--no-prompt",
                    "--launch",
                    "--",
                    context.ApplicationPath
                ],
                context.CapturePath,
                Timeout: context.Duration + TimeSpan.FromSeconds(45),
                InterruptAfter: context.Duration + TimeSpan.FromSeconds(2)),
            context.Log,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess("record the Instruments trace", captureResult, allowInterrupted: true);
        if (!Directory.Exists(tracePath))
        {
            throw new InvalidOperationException(
                "xctrace completed without producing the expected .trace bundle.");
        }

        context.Log("Validating and exporting the Instruments table of contents.");
        var exportResult = await processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                context.CaptureToolPath,
                [
                    "export",
                    "--input",
                    tracePath,
                    "--toc",
                    "--output",
                    tocPath
                ],
                context.CapturePath,
                TimeSpan.FromSeconds(45)),
            context.Log,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess("export the Instruments table of contents", exportResult);
        if (!File.Exists(tocPath) || new FileInfo(tocPath).Length == 0)
        {
            throw new InvalidOperationException(
                "xctrace did not export a usable table of contents for the captured trace.");
        }

        return new NativeProfileAdapterCaptureResult(
            [
                new NativeProfileProducedArtifact(
                    TraceArtifactKind,
                    tracePath,
                    IsDirectory: true,
                    Authoritative: true),
                new NativeProfileProducedArtifact(
                    TableOfContentsArtifactKind,
                    tocPath,
                    IsDirectory: false,
                    Authoritative: false)
            ],
            []);
    }

    private Task<NativeProfilingProcessResult> RunToolAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                XcrunPath,
                arguments,
                Environment.CurrentDirectory,
                timeout),
            outputReceived: null,
            cancellationToken);

    private static string? ReadOutput(NativeProfilingProcessResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return result.IsSuccess && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private static void EnsureSuccess(
        string operation,
        NativeProfilingProcessResult result,
        bool allowInterrupted = false)
    {
        if (result.TimedOut)
        {
            throw new TimeoutException($"xctrace timed out while attempting to {operation}.");
        }

        if (result.IsSuccess || (allowInterrupted && result.WasInterrupted))
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? string.IsNullOrWhiteSpace(result.StandardOutput)
                ? $"exit code {result.ExitCode}"
                : result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException($"xctrace could not {operation}: {detail}");
    }
}
