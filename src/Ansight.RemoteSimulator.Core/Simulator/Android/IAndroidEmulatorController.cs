namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public interface IAndroidEmulatorController
{
    Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default);

    Task SendTouchAsync(
        string deviceSerial,
        IReadOnlyList<AndroidEmulatorTouch> touches,
        CancellationToken cancellationToken = default);
}
