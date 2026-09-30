using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Streaming;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

internal sealed class FakeRemoteSimulatorFrameSource : ISimulatorFrameSource
{
    public Task<RemoteFrame> CaptureAsync(
        string deviceUdid,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteFrame([1], "image/png"));
}

internal sealed class FakeRemoteSimulatorInputSink : ISimulatorInputSink
{
    private readonly TaskCompletionSource<RemotePointerEvent> pointerReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public string BackendName => "fake";

    public string Status => "Ready";

    public Task<InputDeliveryResult> SendAsync(
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken = default)
    {
        pointerReceived.TrySetResult(pointerEvent);
        return Task.FromResult(InputDeliveryResult.Success(BackendName));
    }

    public Task<RemotePointerEvent> WaitForPointerAsync()
        => pointerReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

    public Task<InputDeliveryResult> SendButtonAsync(
        RemoteButtonEvent buttonEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Success(BackendName));

    public Task<InputDeliveryResult> SendKeyAsync(
        RemoteKeyEvent keyEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Success(BackendName));

    public Task<InputDeliveryResult> SendTextAsync(
        RemoteTextEvent textEvent,
        CancellationToken cancellationToken = default)
        => Task.FromResult(InputDeliveryResult.Success(BackendName));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
