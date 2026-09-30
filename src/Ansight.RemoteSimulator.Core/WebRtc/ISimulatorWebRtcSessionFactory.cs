namespace Ansight.RemoteSimulator.Core.WebRtc;

public interface ISimulatorWebRtcSessionFactory
{
    string BackendName { get; }

    string Status { get; }

    bool SupportsDevice(string deviceUdid);

    ISimulatorWebRtcSession Create(string deviceUdid, int framesPerSecond);
}
