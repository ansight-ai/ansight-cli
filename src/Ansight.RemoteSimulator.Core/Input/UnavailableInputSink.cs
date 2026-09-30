namespace Ansight.RemoteSimulator.Core.Input;

public sealed class UnavailableInputSink : ISimulatorInputSink
{
    public UnavailableInputSink(string message)
    {
        Status = string.IsNullOrWhiteSpace(message) ? "Input forwarding is unavailable." : message;
    }

    public string BackendName => "unavailable";

    public string Status { get; }

    public Task<InputDeliveryResult> SendAsync(
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Failure(BackendName, Status));

    public Task<InputDeliveryResult> SendButtonAsync(
        RemoteButtonEvent buttonEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Failure(BackendName, Status));

    public Task<InputDeliveryResult> SendKeyAsync(
        RemoteKeyEvent keyEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Failure(BackendName, Status));

    public Task<InputDeliveryResult> SendTextAsync(
        RemoteTextEvent textEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Failure(BackendName, Status));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
