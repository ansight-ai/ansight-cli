namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record ProcessSampleAdapterContext(
    string CapturePath,
    int ProcessId,
    TimeSpan Duration,
    int IntervalMilliseconds,
    Action<string> Log);

internal interface IProcessSampleCaptureAdapter
{
    string Engine { get; }

    Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken);

    Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        ProcessSampleAdapterContext context,
        CancellationToken cancellationToken);
}

internal sealed class ProcessSampleCaptureAdapter : IProcessSampleCaptureAdapter
{
    internal const string ArtifactKind = "sample-call-graph";
    internal const string DefaultSampleToolPath = "/usr/bin/sample";
    private readonly INativeProfilingProcessRunner processRunner;
    private readonly string sampleToolPath;

    public ProcessSampleCaptureAdapter(
        INativeProfilingProcessRunner processRunner,
        string sampleToolPath = DefaultSampleToolPath)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.sampleToolPath = sampleToolPath;
    }

    public string Engine => "sample";

    public Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isAvailable = OperatingSystem.IsMacOS() && File.Exists(sampleToolPath);
        var message = isAvailable
            ? "macOS process stack sampling is available."
            : OperatingSystem.IsMacOS()
                ? $"The macOS sample tool was not found at '{sampleToolPath}'."
                : "Process stack sampling requires macOS and /usr/bin/sample.";
        return Task.FromResult(new NativeProfileToolchain(
            NativeProfilePlatforms.Process,
            Engine,
            isAvailable,
            isAvailable ? sampleToolPath : null,
            null,
            null,
            message));
    }

    public async Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        ProcessSampleAdapterContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outputPath = Path.Combine(context.CapturePath, "raw", "sample.txt");
        var durationSeconds = checked((int)Math.Ceiling(context.Duration.TotalSeconds));
        context.Log(
            $"Sampling process {context.ProcessId} for {durationSeconds} seconds "
            + $"at {context.IntervalMilliseconds} ms intervals.");
        var result = await processRunner.RunAsync(
            new NativeProfilingProcessRequest(
                sampleToolPath,
                [
                    context.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    context.IntervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-file",
                    outputPath
                ],
                context.CapturePath,
                Timeout: context.Duration + TimeSpan.FromSeconds(15)),
            context.Log,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"The sample process did not complete within {durationSeconds + 15} seconds.");
        }

        if (!result.IsSuccess)
        {
            var detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
            throw new InvalidOperationException(
                "The macOS sample capture failed"
                + (detail is null ? $" with exit code {result.ExitCode}." : $": {detail}"));
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(
                "The macOS sample command completed without producing a call-graph artifact.");
        }

        return new NativeProfileAdapterCaptureResult(
            [
                new NativeProfileProducedArtifact(
                    ArtifactKind,
                    outputPath,
                    IsDirectory: false,
                    Authoritative: true)
            ],
            []);
    }

    private static string? FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
