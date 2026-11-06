using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio.Protocol;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public sealed class AndroidAudioTransportException : Exception
{
    public AndroidAudioTransportException(string status, long submittedFrames) : base($"Android audio gRPC failed ({status}).")
    {
        this.Status = status;
        this.SubmittedFrames = submittedFrames;
    }

    public string Status { get; }
    public long SubmittedFrames { get; }
}
