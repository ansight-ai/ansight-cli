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
