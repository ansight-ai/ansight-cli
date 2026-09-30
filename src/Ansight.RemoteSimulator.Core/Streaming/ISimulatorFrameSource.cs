namespace Ansight.RemoteSimulator.Core.Streaming;

public interface ISimulatorFrameSource
{
    Task<RemoteFrame> CaptureAsync(string deviceUdid, CancellationToken cancellationToken = default);
}
