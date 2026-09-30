using Ansight.Adb;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed class AdbAndroidEmulatorClient : IAndroidEmulatorClient
{
    private readonly AdbClient client;

    public AdbAndroidEmulatorClient(AdbClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => client.GetDevicesAsync(cancellationToken);

    public Task<byte[]> CaptureScreenshotPngAsync(
        string deviceSerial,
        CancellationToken cancellationToken = default)
        => client.CaptureScreenshotPngAsync(deviceSerial, cancellationToken);

    public Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => client.RunAsync(arguments, cancellationToken);

    public Task<AdbCommandResult> RunWithStandardInputAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellationToken = default)
        => client.RunWithStandardInputAsync(arguments, standardInput, cancellationToken);
}
