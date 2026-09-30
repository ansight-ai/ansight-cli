namespace Ansight.Host.Runtime.Screenshots;

using Ansight.Pairing.Models;

internal interface IExternalSessionScreenshotCaptureManager
{
    event EventHandler<ExternalSessionScreenshotCaptureFailedEventArgs>? CaptureFailed;

    Task<ExternalSessionScreenshotCapturePolicy> AttachAsync(
        string sessionId,
        DeviceAppProfile? profile,
        string? profileJson,
        ExternalSessionScreenshotCaptureRequest captureRequest,
        CancellationToken cancellationToken = default);

    Task<byte[]?> CaptureFrameAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<byte[]?>(null);

    IDisposable BeginTestRun(string sessionId);

    void SetInterval(string sessionId, int intervalMilliseconds);

    Task StopAsync(string sessionId, string reason);
}
