using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio.Protocol;

namespace Ansight.Host.Tests.Unit.Audio;

public sealed class AndroidAudioTransportTests
{
    [Theory]
    [InlineData(16000, 1, 737)]
    [InlineData(11025, 2, 2010)]
    [InlineData(48000, 2, 9601)]
    public async Task StreamsExactFramesWithBearerAuthenticationAndBlockingDelivery(int rate, int channels, int frames)
    {
        var bytes = Enumerable.Range(0, frames * channels * 2).Select(value => (byte)(value % 251)).ToArray();
        var invoker = new CapturingInvoker();
        using var client = new AndroidEmulatorAudioClient(invoker, "private-test-token");
        var submitted = await client.InjectAsync(new(bytes, rate, channels, frames), default);
        Assert.Equal(frames, submitted);
        Assert.Equal("/android.emulation.control.EmulatorController/injectAudio", invoker.Method);
        Assert.Equal("Bearer private-test-token", invoker.Options.Headers!.GetValue("authorization"));
        Assert.NotNull(invoker.Options.Deadline);
        Assert.Equal(bytes, invoker.Packets.SelectMany(packet => packet.Audio.ToByteArray()).ToArray());
        Assert.All(invoker.Packets, packet =>
        {
            Assert.Equal((ulong)rate, packet.Format.SamplingRate);
            Assert.Equal(channels - 1, (int)packet.Format.Channels);
            Assert.Equal(1, (int)packet.Format.Format);
            Assert.Equal(0, (int)packet.Format.Mode);
            Assert.True(packet.Audio.Length <= ((rate + 49) / 50) * channels * 2);
        });
        Assert.True(invoker.Completed);
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public async Task ServerFailureDoesNotBecomeSuccessOrExposeServerDetails()
    {
        var invoker = new CapturingInvoker { Error = new RpcException(new Status(StatusCode.Unavailable, "credential-containing server details")) };
        using var client = new AndroidEmulatorAudioClient(invoker, "private-test-token");
        var error = await Assert.ThrowsAsync<AndroidAudioTransportException>(() => client.InjectAsync(new(new byte[640], 16000, 1, 320), default));
        Assert.Equal("Unavailable", error.Status);
        Assert.Equal(320, error.SubmittedFrames);
        Assert.DoesNotContain("credential", error.ToString());
        Assert.DoesNotContain("private-test-token", error.ToString());
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public async Task CancellationDisposesStreamingCallWithoutSubmittingAudio()
    {
        var invoker = new CapturingInvoker();
        using var client = new AndroidEmulatorAudioClient(invoker, "private-test-token");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InjectAsync(new(new byte[640], 16000, 1, 320), cancellation.Token));
        Assert.Empty(invoker.Packets);
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public async Task RejectsIncompletePcmBeforeOpeningRpc()
    {
        var invoker = new CapturingInvoker();
        using var client = new AndroidEmulatorAudioClient(invoker, "token");
        await Assert.ThrowsAsync<ArgumentException>(() => client.InjectAsync(new(new byte[639], 16000, 1, 320), default));
        Assert.Null(invoker.Method);
    }

    private sealed class CapturingInvoker : CallInvoker
    {
        public List<AudioPacket> Packets { get; } = [];
        public string? Method { get; private set; }
        public CallOptions Options { get; private set; }
        public Exception? Error { get; init; }
        public bool Completed { get; set; }
        public bool Disposed { get; private set; }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            Method = method.FullName;
            Options = options;
            var response = Error is null ? Task.FromResult((TResponse)(object)new Empty()) : Task.FromException<TResponse>(Error);
            return new(new CapturingWriter<TRequest>(this), response, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => Disposed = true);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class CapturingWriter<T>(CapturingInvoker invoker) : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message)
        {
            invoker.Packets.Add(((AudioPacket)(object)message!).Clone());
            return Task.CompletedTask;
        }
        public Task CompleteAsync()
        {
            invoker.Completed = true;
            return Task.CompletedTask;
        }
    }
}
