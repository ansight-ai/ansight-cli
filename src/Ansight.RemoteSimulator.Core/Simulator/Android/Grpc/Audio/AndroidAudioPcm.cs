using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio.Protocol;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public sealed record AndroidAudioPcm(byte[] Bytes, int SampleRate, int Channels, long FrameCount);
