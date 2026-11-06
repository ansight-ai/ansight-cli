using Ansight.Adb;
using Ansight.Host.Runtime.Configuration;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

namespace Ansight.Host.Audio.Android;

internal interface IAndroidAudioEnvironment
{
    AndroidAudioEndpoint ResolveEndpoint(string serial);

    bool IsEndpointCurrent(AndroidAudioEndpoint endpoint);

    IAndroidEmulatorAudioClient CreateClient(AndroidAudioEndpoint endpoint);

    Task<AndroidMicrophoneObservation> ReadMicrophoneAsync(string serial, CancellationToken cancellationToken);
}
