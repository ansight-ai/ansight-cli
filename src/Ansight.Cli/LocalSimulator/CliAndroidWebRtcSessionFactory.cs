using Ansight.Adb;
using Ansight.SimulatorRtc.Mac;
using Ansight.RemoteSimulator.Core.Simulator.Android;
using Ansight.RemoteSimulator.Core.WebRtc;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliAndroidWebRtcSessionFactory : ISimulatorWebRtcSessionFactory
{
    private readonly AdbClient adbClient;
    private readonly ScrcpyToolResolution toolResolution;
    private string? lastStreamFailure;

    public CliAndroidWebRtcSessionFactory(
        AdbClient adbClient,
        ScrcpyToolResolution toolResolution)
    {
        this.adbClient = adbClient ?? throw new ArgumentNullException(nameof(adbClient));
        this.toolResolution = toolResolution ?? throw new ArgumentNullException(nameof(toolResolution));
    }

    public string BackendName => "scrcpy-mediacodec-h264-webrtc-cli";

    public string Status => Volatile.Read(ref lastStreamFailure) is { Length: > 0 } failure
        ? $"The last Android companion stream failed: {failure}"
        : $"scrcpy {toolResolution.Version} H.264 WebRTC transport is ready.";

    public bool SupportsDevice(string deviceUdid)
        => toolResolution.IsFound && !string.IsNullOrWhiteSpace(deviceUdid);

    public ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond)
    {
        Volatile.Write(ref lastStreamFailure, null);

        return new CliAndroidWebRtcSession(
            adbClient,
            toolResolution,
            deviceUdid,
            framesPerSecond,
            failure => Volatile.Write(ref lastStreamFailure, failure));
    }
}
