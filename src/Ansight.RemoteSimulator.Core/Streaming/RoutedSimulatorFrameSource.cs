using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.Streaming;

public sealed class RoutedSimulatorFrameSource : ISimulatorFrameSource
{
    private readonly IRemoteRuntimeSource runtimeSource;
    private readonly IReadOnlyDictionary<string, ISimulatorFrameSource> sourcesByPlatform;

    public RoutedSimulatorFrameSource(
        IRemoteRuntimeSource runtimeSource,
        IReadOnlyDictionary<string, ISimulatorFrameSource> sourcesByPlatform)
    {
        this.runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        this.sourcesByPlatform = sourcesByPlatform ?? throw new ArgumentNullException(nameof(sourcesByPlatform));
    }

    public Task<RemoteFrame> CaptureAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
    {
        var platform = ResolvePlatform(deviceUdid);
        if (!sourcesByPlatform.TryGetValue(platform, out var source))
        {
            throw new InvalidOperationException($"Frame capture is unavailable for {platform} runtimes.");
        }

        return source.CaptureAsync(deviceUdid, cancellationToken);
    }

    private string ResolvePlatform(string deviceUdid)
        => runtimeSource.Current.Devices.FirstOrDefault(device =>
                string.Equals(device.Identifier, deviceUdid, StringComparison.OrdinalIgnoreCase))?.Platform
           ?? throw new InvalidOperationException("The selected runtime is no longer available.");
}
