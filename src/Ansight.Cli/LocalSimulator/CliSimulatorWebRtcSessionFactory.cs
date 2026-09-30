using Ansight.SimulatorRtc.Mac;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliSimulatorWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
{
    private readonly string developerDirectory;

    public CliSimulatorWebRtcSessionFactory(string developerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerDirectory);
        this.developerDirectory = developerDirectory;
    }

    public string BackendName => "simulator-kit-iosurface-videotoolbox-webrtc-cli";

    public string Status => "iOS Simulator IOSurface capture with hardware H.264 WebRTC transport is ready.";

    public bool SupportsDevice(string deviceUdid)
        => OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(deviceUdid);

    public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
        => new CliSimulatorWebRtcSession(
            new SimulatorRtcSession(
                developerDirectory,
                deviceUdid,
                framesPerSecond,
                averageBitRate: framesPerSecond == 60 ? 8_000_000 : 5_000_000));
}

internal sealed class CliSimulatorWebRtcSession : ISimulatorWebRtcSession
{
    private readonly SimulatorRtcSession session;
    private bool disposed;

    public CliSimulatorWebRtcSession(SimulatorRtcSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        session.InputReceived += HandleInputReceived;
    }

    public event EventHandler<WebRtcInputMessageEventArgs>? InputReceived;

    public string DeviceUdid => session.DeviceUdid;

    public async Task<WebRtcSessionDescription> CreateAnswerAsync(
        WebRtcSessionDescription offer,
        CancellationToken cancellationToken = default)
    {
        var answer = await session.CreateAnswerAsync(
            new SimulatorRtcDescription(offer.Type, offer.Sdp),
            cancellationToken).ConfigureAwait(false);
        return new WebRtcSessionDescription(answer.Type, answer.Sdp);
    }

    public bool TrySendMessage(string message) => session.TrySendMessage(message);

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        session.InputReceived -= HandleInputReceived;
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    private void HandleInputReceived(object? sender, SimulatorRtcInputEventArgs args)
        => InputReceived?.Invoke(this, new WebRtcInputMessageEventArgs(args.Message));
}
