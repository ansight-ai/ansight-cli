using System.Diagnostics;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Configuration;
using Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

namespace Ansight.Host.Audio.Android;

internal sealed class AndroidAudioInjectionProvider : IAudioInjectionProvider
{
    private const string GuardCaveat = "Guest capture observation cannot atomically guarantee a valid native emulator input stream. Emulator 37.1.11 has a known native injection crash; this guard is a mitigation.";
    private readonly IAndroidAudioEnvironment environment;

    public AndroidAudioInjectionProvider(RuntimeOptions options) : this(new AndroidAudioEnvironment(options)) { }
    internal AndroidAudioInjectionProvider(IAndroidAudioEnvironment environment) => this.environment = environment;

    public string Platform => "android";
    public string Backend => "android-emulator-grpc";
    public string GetLeaseKey(AudioTarget target) => $"android-emulator:{target.DeviceId}";

    public async Task<AudioProviderCapabilities> GetCapabilitiesAsync(AudioTarget target, CancellationToken cancellationToken)
    {
        var diagnostics = BaseDiagnostics();
        try
        {
            var endpoint = environment.ResolveEndpoint(target.DeviceId);
            AddEndpoint(diagnostics, endpoint);
            using var client = environment.CreateClient(endpoint);
            var hostEnabled = await client.GetHostMicrophoneEnabledAsync(cancellationToken).ConfigureAwait(false);
            diagnostics["transportAvailable"] = true;
            diagnostics["hostMicrophoneEnabled"] = hostEnabled;
            if (hostEnabled) { return new(false, "host-microphone-enabled", "Disable emulator host microphone access before injecting synthetic audio.", diagnostics); }
            var observation = await environment.ReadMicrophoneAsync(target.DeviceId, cancellationToken).ConfigureAwait(false);
            AddObservation(diagnostics, observation);
            // Capabilities are queried before the task starts recording. Readiness is
            // enforced by InjectAsync, including its bounded wait and final stream check.
            if (observation.State == AndroidMicrophoneState.NotReady)
            {
                return new(true, "available", "Audio injection is supported. Start recording before injection; active guest capture will be checked at delivery.", diagnostics);
            }
            return new(observation.IsReady, observation.Code, observation.Message, diagnostics);
        }
        catch (AndroidAudioEndpointException error) { return new(false, error.Code, error.Message, diagnostics); }
        catch (AndroidAudioTransportException error)
        {
            diagnostics["grpcStatus"] = error.Status;
            return new(false, "endpoint-unavailable", "The emulator's authenticated audio endpoint could not be reached.", diagnostics);
        }
        catch (AudioInjectionException error) { return new(false, error.Code, error.Message, diagnostics); }
    }

    public async Task<AudioProviderDelivery> InjectAsync(AudioTarget target, AudioFixture fixture, int waitForMicrophoneMs, CancellationToken cancellationToken)
    {
        if (waitForMicrophoneMs is < 0 or > 10000) { throw new AudioInjectionException("invalid-timeout", "Microphone readiness wait must be between 0 and 10000 milliseconds."); }
        var diagnostics = BaseDiagnostics();
        long submitted = 0;
        try
        {
            var endpoint = environment.ResolveEndpoint(target.DeviceId);
            AddEndpoint(diagnostics, endpoint);
            using var client = environment.CreateClient(endpoint);
            await RequireDisabledHostMicrophoneAsync(client, diagnostics, cancellationToken).ConfigureAwait(false);
            diagnostics["transportAvailable"] = true;
            var observation = await WaitForMicrophoneAsync(target.DeviceId, waitForMicrophoneMs, cancellationToken).ConfigureAwait(false);
            AddObservation(diagnostics, observation);
            // Recheck host state before the final guest observation, keeping the last observation-to-RPC gap small.
            await RequireDisabledHostMicrophoneAsync(client, diagnostics, cancellationToken).ConfigureAwait(false);
            if (!environment.IsEndpointCurrent(endpoint)) { throw new AudioInjectionException("endpoint-changed", "The emulator endpoint changed during preflight; no audio was injected.", diagnostics); }
            var finalObservation = await environment.ReadMicrophoneAsync(target.DeviceId, cancellationToken).ConfigureAwait(false);
            AddObservation(diagnostics, finalObservation);
            RequireReady(finalObservation, diagnostics);
            if (observation.StreamIdentity != finalObservation.StreamIdentity)
            {
                throw new AudioInjectionException("microphone-not-ready", "The guest microphone stream changed during preflight; no audio was injected.", diagnostics);
            }
            diagnostics["preflightCompletedUtc"] = DateTimeOffset.UtcNow;
            diagnostics["deliveryStarted"] = true;
            submitted = await client.InjectAsync(new AndroidAudioPcm(fixture.PcmBytes, fixture.Info.SampleRate, fixture.Info.Channels, fixture.Info.FrameCount), cancellationToken).ConfigureAwait(false);
            var enabledAfter = await client.GetHostMicrophoneEnabledAsync(cancellationToken).ConfigureAwait(false);
            diagnostics["hostMicrophoneEnabledAfter"] = enabledAfter;
            diagnostics["submittedFrames"] = submitted;
            if (enabledAfter)
            {
                throw new AudioInjectionException("host-microphone-enabled", "Host microphone access changed during injection; synthetic-only delivery cannot be verified.", diagnostics);
            }
            return new("emulator-stream-accepted", submitted, diagnostics);
        }
        catch (AndroidAudioEndpointException error) { throw new AudioInjectionException(error.Code, error.Message, diagnostics); }
        catch (AndroidAudioTransportException error)
        {
            diagnostics["grpcStatus"] = error.Status;
            diagnostics["submittedFrames"] = Math.Max(submitted, error.SubmittedFrames);
            throw new AudioInjectionException("delivery-failed", "The emulator audio stream failed. No automatic retry or restart was attempted.", diagnostics);
        }
        catch (AudioInjectionException error) when (error.Diagnostics is null)
        {
            throw new AudioInjectionException(error.Code, error.Message, diagnostics);
        }
    }

    private async Task<AndroidMicrophoneObservation> WaitForMicrophoneAsync(string serial, int waitMs, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(waitMs > 0 ? waitMs : 2000));
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                var observation = await environment.ReadMicrophoneAsync(serial, budget.Token).ConfigureAwait(false);
                if (observation.State == AndroidMicrophoneState.Unhealthy) { RequireReady(observation, BaseDiagnostics()); }
                if (observation.IsReady)
                {
                    await Task.Delay(100, budget.Token).ConfigureAwait(false);
                    return observation;
                }
                if (elapsed.ElapsedMilliseconds >= waitMs) { RequireReady(observation, BaseDiagnostics()); }
                await Task.Delay((int)Math.Clamp(waitMs - elapsed.ElapsedMilliseconds, 1, 50), budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AudioInjectionException("microphone-not-ready", "The bounded microphone readiness wait expired; no audio was injected.", BaseDiagnostics());
        }
    }

    private static async Task RequireDisabledHostMicrophoneAsync(IAndroidEmulatorAudioClient client, JsonObject diagnostics, CancellationToken cancellationToken)
    {
        var enabled = await client.GetHostMicrophoneEnabledAsync(cancellationToken).ConfigureAwait(false);
        diagnostics["hostMicrophoneEnabledBefore"] = enabled;
        if (enabled) { throw new AudioInjectionException("host-microphone-enabled", "Disable emulator host microphone access before injecting synthetic audio.", diagnostics); }
    }

    private static void RequireReady(AndroidMicrophoneObservation observation, JsonObject diagnostics)
    {
        AddObservation(diagnostics, observation);
        if (!observation.IsReady) { throw new AudioInjectionException(observation.Code, observation.Message, diagnostics); }
    }

    private static JsonObject BaseDiagnostics() => new()
    {
        ["transportAvailable"] = false,
        ["deliveryStarted"] = false,
        ["appCaptureVerified"] = false,
        ["requiresActiveGuestCapture"] = true,
        ["hostMicrophoneModified"] = false,
        ["deliveryMode"] = "MODE_UNSPECIFIED",
        ["packetMilliseconds"] = 20,
        ["readinessGuard"] = "audioflinger-active-quiet-input",
        ["warning"] = GuardCaveat,
    };

    private static void AddEndpoint(JsonObject diagnostics, AndroidAudioEndpoint endpoint)
    {
        diagnostics["emulatorProcessId"] = endpoint.ProcessId;
        diagnostics["endpointPort"] = endpoint.Port;
        diagnostics["authenticated"] = true;
    }

    private static void AddObservation(JsonObject diagnostics, AndroidMicrophoneObservation observation)
    {
        diagnostics["microphoneReady"] = observation.IsReady;
        diagnostics["microphoneState"] = observation.State.ToString();
        diagnostics["lastReadAgeMs"] = observation.LastReadAgeMs;
        diagnostics["maximumPreInjectionPowerDb"] = observation.MaximumPowerDb is { } power && double.IsFinite(power) ? JsonValue.Create(power) : null;
        diagnostics["quietInputObserved"] = observation.IsReady;
    }
}
