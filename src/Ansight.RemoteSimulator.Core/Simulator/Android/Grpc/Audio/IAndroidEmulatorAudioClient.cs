using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio.Protocol;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public interface IAndroidEmulatorAudioClient : IDisposable
{
    Task<bool> GetHostMicrophoneEnabledAsync(CancellationToken cancellationToken);

    Task<long> InjectAsync(AndroidAudioPcm pcm, CancellationToken cancellationToken);
}
