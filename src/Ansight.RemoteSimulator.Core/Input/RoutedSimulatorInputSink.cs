using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.Input;

public sealed class RoutedSimulatorInputSink : ISimulatorInputSink
{
    private readonly IRemoteRuntimeSource runtimeSource;
    private readonly IReadOnlyDictionary<string, ISimulatorInputSink> sinksByPlatform;
    private bool disposed;

    public RoutedSimulatorInputSink(
        IRemoteRuntimeSource runtimeSource,
        IReadOnlyDictionary<string, ISimulatorInputSink> sinksByPlatform)
    {
        this.runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        this.sinksByPlatform = sinksByPlatform ?? throw new ArgumentNullException(nameof(sinksByPlatform));
    }

    public string BackendName => string.Join(
        "+",
        sinksByPlatform.Values.Select(static sink => sink.BackendName).Distinct(StringComparer.Ordinal));

    public string Status => string.Join(
        " ",
        sinksByPlatform
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}: {pair.Value.Status}"));

    public Task<InputDeliveryResult> SendAsync(
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken = default)
        => ResolveSink(pointerEvent.DeviceUdid).SendAsync(pointerEvent, cancellationToken);

    public Task<InputDeliveryResult> SendButtonAsync(
        RemoteButtonEvent buttonEvent,
        CancellationToken cancellationToken = default)
        => ResolveSink(buttonEvent.DeviceUdid).SendButtonAsync(buttonEvent, cancellationToken);

    public Task<InputDeliveryResult> SendKeyAsync(
        RemoteKeyEvent keyEvent,
        CancellationToken cancellationToken = default)
        => ResolveSink(keyEvent.DeviceUdid).SendKeyAsync(keyEvent, cancellationToken);

    public Task<InputDeliveryResult> SendTextAsync(
        RemoteTextEvent textEvent,
        CancellationToken cancellationToken = default)
        => ResolveSink(textEvent.DeviceUdid).SendTextAsync(textEvent, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var sink in sinksByPlatform.Values.Distinct())
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ISimulatorInputSink ResolveSink(string deviceUdid)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var platform = runtimeSource.Current.Devices.FirstOrDefault(device =>
                string.Equals(device.Identifier, deviceUdid, StringComparison.OrdinalIgnoreCase))?.Platform
            ?? throw new InvalidOperationException("The selected runtime is no longer available.");
        return sinksByPlatform.TryGetValue(platform, out var sink)
            ? sink
            : throw new InvalidOperationException($"Input forwarding is unavailable for {platform} runtimes.");
    }
}
