namespace Ansight.RemoteSimulator.Core.Input;

public interface ISimulatorInputSink : IAsyncDisposable
{
    string BackendName { get; }

    string Status { get; }

    Task<InputDeliveryResult> SendAsync(RemotePointerEvent pointerEvent, CancellationToken cancellationToken = default);

    Task<InputDeliveryResult> SendButtonAsync(RemoteButtonEvent buttonEvent, CancellationToken cancellationToken = default);

    Task<InputDeliveryResult> SendKeyAsync(RemoteKeyEvent keyEvent, CancellationToken cancellationToken = default);

    Task<InputDeliveryResult> SendTextAsync(RemoteTextEvent textEvent, CancellationToken cancellationToken = default);
}
