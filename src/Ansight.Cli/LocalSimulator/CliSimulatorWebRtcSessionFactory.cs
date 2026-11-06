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
