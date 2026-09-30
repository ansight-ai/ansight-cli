using Ansight.Adb;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public interface IAndroidEmulatorClient
{
    Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RunWithStandardInputAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken = default);
}
