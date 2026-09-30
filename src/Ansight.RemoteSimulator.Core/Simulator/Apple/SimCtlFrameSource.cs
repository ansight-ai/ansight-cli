using Ansight.SimCtl;
using Ansight.RemoteSimulator.Core.Streaming;

namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public sealed class SimCtlFrameSource : ISimulatorFrameSource, IDisposable
{
    private readonly SimCtlToolResolution toolResolution;
    private readonly ISimCtlCommandRunner commandRunner;
    private readonly SemaphoreSlim captureGate = new(1, 1);

    public SimCtlFrameSource(
        SimCtlToolResolution toolResolution,
        ISimCtlCommandRunner? commandRunner = null)
    {
        if (toolResolution is null || !toolResolution.IsFound)
        {
            throw new ArgumentException("A resolved SimCtl installation is required.", nameof(toolResolution));
        }

        this.toolResolution = toolResolution;
        this.commandRunner = commandRunner ?? new SimCtlClientCommandRunner();
    }

    public async Task<RemoteFrame> CaptureAsync(string deviceUdid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-simulator-frame-{Guid.NewGuid():N}.jpg");
        try
        {
            var result = await commandRunner.RunAsync(
                toolResolution,
                ["simctl", "io", deviceUdid, "screenshot", "--type=jpeg", outputPath],
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Simulator screenshot failed: {result.StandardError.Trim()}");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("Simulator screenshot completed without producing an image.");
            }

            var frame = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (!IsJpeg(frame))
            {
                throw new InvalidOperationException("Simulator screenshot did not produce a valid JPEG image.");
            }

            return RemoteFrame.Jpeg(frame);
        }
        finally
        {
            TryDelete(outputPath);
            captureGate.Release();
        }
    }

    public void Dispose() => captureGate.Dispose();

    private static bool IsJpeg(byte[] frame)
        => frame.Length >= 4
           && frame[0] == 0xFF
           && frame[1] == 0xD8
           && frame[^2] == 0xFF
           && frame[^1] == 0xD9;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }
        catch (UnauthorizedAccessException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }
    }

    private sealed class SimCtlClientCommandRunner : ISimCtlCommandRunner
    {
        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution resolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
            => new SimCtlClient(resolution).RunXcrunAsync(arguments, cancellationToken);
    }
}
