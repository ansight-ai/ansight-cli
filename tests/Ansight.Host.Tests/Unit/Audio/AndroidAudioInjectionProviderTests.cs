using Ansight.Host.Audio;
using Ansight.Host.Audio.Android;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

namespace Ansight.Host.Tests.Unit.Audio;

public sealed class AndroidAudioInjectionProviderTests
{
    private static readonly AudioTarget Target = new("live-session", "harness", "emulator-5554", "android");
    private static readonly AudioFixture Fixture = new("fixture.wav", new("sha256", 20, 16000, 1, 16, 320), new byte[640]);
    private static readonly AndroidMicrophoneObservation Ready = new(AndroidMicrophoneState.Ready, "ready", "ready", "input-1", -78, 10);
    private static readonly AndroidMicrophoneObservation NotReady = new(AndroidMicrophoneState.NotReady, "microphone-not-ready", "closed");

    [Fact]
    public async Task RejectsEnabledHostMicrophoneWithoutReadingGuestOrInjecting()
    {
        var environment = new FakeEnvironment { HostEnabled = true };
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.Equal("host-microphone-enabled", error.Code);
        Assert.Equal(0, environment.Reads);
        Assert.Equal(0, environment.Injections);
    }

    [Theory]
    [InlineData("NotReady", "microphone-not-ready")]
    [InlineData("Unknown", "microphone-state-unknown")]
    [InlineData("Unhealthy", "emulator-input-unhealthy")]
    public async Task FailedPreflightNeverCallsInjection(string state, string code)
    {
        var environment = new FakeEnvironment { Observation = new(Enum.Parse<AndroidMicrophoneState>(state), code, "not safe") };
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task AdbReadinessFailureExplicitlyReportsNoDeliveryStarted()
    {
        var environment = new FakeEnvironment { ReadError = new AudioInjectionException("microphone-state-unknown", "ADB could not read capture state.") };
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.False(error.Diagnostics!["deliveryStarted"]!.GetValue<bool>());
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task BoundedWaitCanObserveCaptureOpeningBeforeDelivery()
    {
        var environment = new FakeEnvironment();
        environment.Observations.Enqueue(NotReady);
        environment.Observations.Enqueue(Ready);
        environment.Observations.Enqueue(Ready);
        await new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 1000, default);
        Assert.Equal(3, environment.Reads);
        Assert.Equal(1, environment.Injections);
    }

    [Fact]
    public async Task RechecksMicrophoneBeforeInjectionAndRefusesAClosedStream()
    {
        var environment = new FakeEnvironment();
        environment.Observations.Enqueue(Ready);
        environment.Observations.Enqueue(NotReady);
        await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.Equal(2, environment.Reads);
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task RejectsEndpointChangesWithoutInjecting()
    {
        var environment = new FakeEnvironment { EndpointCurrent = false };
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.Equal("endpoint-changed", error.Code);
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task SuccessfulDeliveryOnlyClaimsTransportAcceptance()
    {
        var environment = new FakeEnvironment();
        var result = await new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default);
        Assert.Equal("emulator-stream-accepted", result.CompletionKind);
        Assert.Equal(320, result.SubmittedFrames);
        Assert.False(result.Diagnostics["appCaptureVerified"]!.GetValue<bool>());
        Assert.False(result.Diagnostics["hostMicrophoneEnabledAfter"]!.GetValue<bool>());
        Assert.True(environment.Disposed);
        Assert.Equal(1, environment.Injections);
    }

    [Fact]
    public async Task CapabilitiesDistinguishAvailableTransportFromClosedMicrophone()
    {
        var environment = new FakeEnvironment { Observation = NotReady };
        var result = await new AndroidAudioInjectionProvider(environment).GetCapabilitiesAsync(Target, default);
        Assert.True(result.Available);
        Assert.Equal("available", result.Code);
        Assert.True(result.Diagnostics["transportAvailable"]!.GetValue<bool>());
        Assert.False(result.Diagnostics["microphoneReady"]!.GetValue<bool>());
        Assert.Equal(0, environment.Injections);
    }

    [Theory]
    [InlineData("Unknown", "microphone-state-unknown")]
    [InlineData("Unhealthy", "emulator-input-unhealthy")]
    public async Task CapabilitiesStillRejectUnverifiableOrUnhealthyInput(string state, string code)
    {
        var environment = new FakeEnvironment { Observation = new(Enum.Parse<AndroidMicrophoneState>(state), code, "not safe") };
        var result = await new AndroidAudioInjectionProvider(environment).GetCapabilitiesAsync(Target, default);
        Assert.False(result.Available);
        Assert.Equal(code, result.Code);
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task CapabilitiesStillRejectHostMicrophoneAccess()
    {
        var environment = new FakeEnvironment { HostEnabled = true };
        var result = await new AndroidAudioInjectionProvider(environment).GetCapabilitiesAsync(Target, default);
        Assert.False(result.Available);
        Assert.Equal("host-microphone-enabled", result.Code);
        Assert.Equal(0, environment.Reads);
        Assert.Equal(0, environment.Injections);
    }

    [Fact]
    public async Task CancellationDuringReadinessDisposesClientAndNeverInjects()
    {
        var environment = new FakeEnvironment { Observation = NotReady };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 1000, cancellation.Token));
        Assert.Equal(0, environment.Injections);
        Assert.True(environment.Disposed);
    }

    [Fact]
    public async Task DeliveryFailurePreservesPartialCountAndDoesNotRetry()
    {
        var environment = new FakeEnvironment { DeliveryError = new AndroidAudioTransportException("Unavailable", 100) };
        var error = await Assert.ThrowsAsync<AudioInjectionException>(() => new AndroidAudioInjectionProvider(environment).InjectAsync(Target, Fixture, 0, default));
        Assert.Equal("delivery-failed", error.Code);
        Assert.Equal(100, error.Diagnostics!["submittedFrames"]!.GetValue<long>());
        Assert.Equal(1, environment.Injections);
    }

    private sealed class FakeEnvironment : IAndroidAudioEnvironment, IAndroidEmulatorAudioClient
    {
        public bool HostEnabled { get; init; }
        public bool EndpointCurrent { get; init; } = true;
        public AndroidMicrophoneObservation Observation { get; init; } = Ready;
        public Queue<AndroidMicrophoneObservation> Observations { get; } = new();
        public Exception? DeliveryError { get; init; }
        public Exception? ReadError { get; init; }
        public int Reads { get; private set; }
        public int Injections { get; private set; }
        public bool Disposed { get; private set; }

        public AndroidAudioEndpoint ResolveEndpoint(string serial) => new(serial, 123, 456, 9876, "token");
        public bool IsEndpointCurrent(AndroidAudioEndpoint endpoint) => EndpointCurrent;
        public IAndroidEmulatorAudioClient CreateClient(AndroidAudioEndpoint endpoint) => this;
        public Task<AndroidMicrophoneObservation> ReadMicrophoneAsync(string serial, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (ReadError is not null) { return Task.FromException<AndroidMicrophoneObservation>(ReadError); }
            return Task.FromResult(Observations.Count > 0 ? Observations.Dequeue() : Observation);
        }
        public Task<bool> GetHostMicrophoneEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(HostEnabled);
        public Task<long> InjectAsync(AndroidAudioPcm pcm, CancellationToken cancellationToken)
        {
            Injections++;
            return DeliveryError is null ? Task.FromResult(pcm.FrameCount) : Task.FromException<long>(DeliveryError);
        }
        public void Dispose() => Disposed = true;
    }
}
