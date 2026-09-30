using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio.Protocol;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public sealed record AndroidAudioPcm(byte[] Bytes, int SampleRate, int Channels, long FrameCount);

public interface IAndroidEmulatorAudioClient : IDisposable
{
    Task<bool> GetHostMicrophoneEnabledAsync(CancellationToken cancellationToken);
    Task<long> InjectAsync(AndroidAudioPcm pcm, CancellationToken cancellationToken);
}

public sealed class AndroidAudioTransportException(string status, long submittedFrames) : Exception($"Android audio gRPC failed ({status}).")
{
    public string Status { get; } = status;
    public long SubmittedFrames { get; } = submittedFrames;
}

public sealed class AndroidEmulatorAudioClient : IAndroidEmulatorAudioClient
{
    private readonly GrpcChannel? channel;
    private readonly EmulatorController.EmulatorControllerClient client;
    private readonly string token;

    public AndroidEmulatorAudioClient(AndroidAudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        token = endpoint.Token;
        channel = GrpcChannel.ForAddress(endpoint.Address, new GrpcChannelOptions
        {
            ThrowOperationCanceledOnCancellation = true,
            MaxReceiveMessageSize = 65536,
            MaxSendMessageSize = 65536,
            HttpHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) },
        });
        client = new EmulatorController.EmulatorControllerClient(channel);
    }

    internal AndroidEmulatorAudioClient(CallInvoker invoker, string token)
    {
        this.token = token;
        client = new EmulatorController.EmulatorControllerClient(invoker);
    }

    public async Task<bool> GetHostMicrophoneEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var call = client.getMicrophoneStateAsync(new Empty(), Headers(), DateTime.UtcNow.AddSeconds(3), cancellationToken);
            return (await call.ResponseAsync.ConfigureAwait(false)).RealAudioEnabled;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException error) { throw new AndroidAudioTransportException(error.StatusCode.ToString(), 0); }
    }

    public async Task<long> InjectAsync(AndroidAudioPcm pcm, CancellationToken cancellationToken)
    {
        Validate(pcm);
        long submitted = 0;
        try
        {
            using var call = client.injectAudio(Headers(), DateTime.UtcNow.AddSeconds((double)pcm.FrameCount / pcm.SampleRate + 8), cancellationToken);
            foreach (var packet in CreatePackets(pcm))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await call.RequestStream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                submitted += packet.Audio.Length / (pcm.Channels * 2);
            }
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            await call.ResponseAsync.ConfigureAwait(false);
            if (submitted != pcm.FrameCount) { throw new AndroidAudioTransportException("IncompleteStream", submitted); }
            return submitted;
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException error) { throw new AndroidAudioTransportException(error.StatusCode.ToString(), submitted); }
    }

    internal static IEnumerable<AudioPacket> CreatePackets(AndroidAudioPcm pcm)
    {
        Validate(pcm);
        var timestamp = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        long offset = 0;
        long packetNumber = 0;
        while (offset < pcm.FrameCount)
        {
            packetNumber++;
            var end = Math.Min(pcm.FrameCount, Math.Max(offset + 1, packetNumber * pcm.SampleRate / 50));
            yield return new AudioPacket
            {
                Format = new AudioFormat
                {
                    SamplingRate = (ulong)pcm.SampleRate,
                    Channels = (AudioFormat.Types.Channels)(pcm.Channels - 1),
                    Format = AudioFormat.Types.SampleFormat.AudFmtS16,
                    Mode = AudioFormat.Types.DeliveryMode.ModeUnspecified,
                },
                Timestamp = timestamp + (ulong)(offset * 1_000_000 / pcm.SampleRate),
                Audio = ByteString.CopyFrom(pcm.Bytes, checked((int)(offset * pcm.Channels * 2)), checked((int)((end - offset) * pcm.Channels * 2))),
            };
            offset = end;
        }
    }

    private static void Validate(AndroidAudioPcm pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.SampleRate is < 1 or > 48000 || pcm.Channels is < 1 or > 2 || pcm.FrameCount <= 0
            || pcm.FrameCount > pcm.SampleRate * 600L || pcm.Bytes.LongLength != pcm.FrameCount * pcm.Channels * 2)
        {
            throw new ArgumentException("Audio must contain complete bounded PCM16 frames at no more than 48 kHz.", nameof(pcm));
        }
    }

    private Metadata Headers() => new() { { "authorization", $"Bearer {token}" } };
    public void Dispose() => channel?.Dispose();
}
