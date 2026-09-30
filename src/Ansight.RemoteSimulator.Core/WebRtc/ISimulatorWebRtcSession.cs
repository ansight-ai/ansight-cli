namespace Ansight.RemoteSimulator.Core.WebRtc;

public interface ISimulatorWebRtcSession : IAsyncDisposable
{
    event EventHandler<WebRtcInputMessageEventArgs>? InputReceived;

    string DeviceUdid { get; }

    Task<WebRtcSessionDescription> CreateAnswerAsync(
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default);

    bool TrySendMessage(string message);
}
