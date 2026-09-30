using Ansight.RemoteSimulator.Core.Streaming;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed class AndroidEmulatorFrameSource : ISimulatorFrameSource, IDisposable
{
    private readonly IAndroidEmulatorClient client;
    private readonly IAndroidEmulatorController? controller;
    private readonly SemaphoreSlim captureGate = new(1, 1);

    public AndroidEmulatorFrameSource(
        IAndroidEmulatorClient client,
        IAndroidEmulatorController? controller = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.controller = controller;
    }

    public async Task<RemoteFrame> CaptureAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceUdid);
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] content;
            if (controller is not null)
            {
                try
                {
                    content = await controller
                        .CaptureScreenshotPngAsync(deviceUdid, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                    content = await client
                        .CaptureScreenshotPngAsync(deviceUdid, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                content = await client
                    .CaptureScreenshotPngAsync(deviceUdid, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!IsPng(content))
            {
                throw new InvalidOperationException("Android emulator capture did not produce a valid PNG image.");
            }

            return RemoteFrame.Png(content);
        }
        finally
        {
            captureGate.Release();
        }
    }

    public void Dispose()
    {
        (controller as IDisposable)?.Dispose();
        captureGate.Dispose();
    }

    private static bool IsPng(byte[] content)
        => content.Length >= 8
           && content[0] == 0x89
           && content[1] == 0x50
           && content[2] == 0x4E
           && content[3] == 0x47
           && content[4] == 0x0D
           && content[5] == 0x0A
           && content[6] == 0x1A
           && content[7] == 0x0A;
}
