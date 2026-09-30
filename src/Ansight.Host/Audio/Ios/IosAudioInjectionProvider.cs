using System.Globalization;
using System.Text.Json.Nodes;
using Ansight.SimCtl;

namespace Ansight.Host.Audio.Ios;

internal sealed class IosAudioInjectionProvider : IAudioInjectionProvider
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<JsonObject>> runHelper;
    private readonly Func<CancellationToken, Task<string?>> resolveSimulatorApp;
    private readonly bool isMacOs;
    private readonly string deviceUid;

    public IosAudioInjectionProvider(RuntimeOptions options)
        : this(IosAudioHelper.RunAsync, async cancellationToken =>
        {
            var resolution = await SimCtlToolLocator.ResolveAsync(options.XcodePath, cancellationToken).ConfigureAwait(false);
            return resolution.IsFound ? Path.Combine(resolution.DeveloperDirectory, "Applications", "Simulator.app") : null;
        }, OperatingSystem.IsMacOS(), Environment.GetEnvironmentVariable(IosAudioHelper.DeviceUidEnvironmentVariable))
    {
    }

    internal IosAudioInjectionProvider(
        Func<IReadOnlyList<string>, CancellationToken, Task<JsonObject>> runHelper,
        Func<CancellationToken, Task<string?>> resolveSimulatorApp,
        bool isMacOs,
        string? deviceUid)
    {
        this.runHelper = runHelper;
        this.resolveSimulatorApp = resolveSimulatorApp;
        this.isMacOs = isMacOs;
        this.deviceUid = string.IsNullOrWhiteSpace(deviceUid) ? IosAudioHelper.DefaultDeviceUid : deviceUid.Trim();
    }

    public string Platform => "ios";
    public string Backend => "coreaudio-loopback";

    // Simulator input selection and loopback audio are shared across the host.
    public string GetLeaseKey(AudioTarget target) => "ios:simulator-audio-route";

    public async Task<AudioProviderCapabilities> GetCapabilitiesAsync(AudioTarget target, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = await BuildArgumentsAsync("inspect", target, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            JsonObject response;
            try { response = await runHelper(arguments, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AudioInjectionException("audio-route-probe-timeout", "Simulator audio route inspection timed out."); }
            var diagnostics = ValidateRoute(response, target);
            return new(true, "available", "The configured Simulator input selection is verified. Active routing is checked when the app records; playback alone does not prove transcription.", diagnostics);
        }
        catch (AudioInjectionException exception)
        {
            return new(false, exception.Code, exception.Message, exception.Diagnostics ?? new JsonObject());
        }
    }

    public async Task<AudioProviderDelivery> InjectAsync(AudioTarget target, AudioFixture fixture,
        int waitForMicrophoneMs, CancellationToken cancellationToken)
    {
        if (waitForMicrophoneMs is < 0 or > 10000)
            throw new AudioInjectionException("invalid-wait-for-microphone", "Microphone readiness timeout must be between 0 and 10000 milliseconds.", new() { ["deliveryStarted"] = false });
        var arguments = await BuildArgumentsAsync("inject", target, cancellationToken).ConfigureAwait(false);
        arguments.Add("--file");
        arguments.Add(fixture.Path);
        arguments.Add("--wait-ms");
        arguments.Add(waitForMicrophoneMs.ToString(CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(fixture.Info.DurationMs + waitForMicrophoneMs + 30000));
        JsonObject response;
        try { response = await runHelper(arguments, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AudioInjectionException("audio-delivery-timeout", "The iOS audio operation exceeded its playback deadline and was stopped."); }
        var diagnostics = ValidateRoute(response, target);
        var completionKind = diagnostics["completionKind"]?.GetValue<string>();
        var submittedFrames = diagnostics["submittedFrames"]?.GetValue<long>() ?? 0;
        if (completionKind != "host-output-played" || submittedFrames != fixture.Info.FrameCount)
            throw new AudioInjectionException("audio-delivery-incomplete", "The audio helper did not confirm playback of every fixture frame.", diagnostics);
        return new(completionKind, submittedFrames, diagnostics);
    }

    private async Task<List<string>> BuildArgumentsAsync(string command, AudioTarget target, CancellationToken token)
    {
        if (!isMacOs) throw new AudioInjectionException("platform-unavailable", "iOS Simulator audio injection requires a local macOS host.");
        if (!string.Equals(target.Platform, Platform, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(target.DeviceId, out _))
            throw new AudioInjectionException("unsupported-device", "Audio injection requires an exact iOS Simulator UDID from the running session.");
        var simulatorApp = await resolveSimulatorApp(token).ConfigureAwait(false);
        if (simulatorApp is null) throw new AudioInjectionException("xcode-unavailable", "The configured Xcode Simulator application could not be resolved.");
        return [command, "--device-uid", deviceUid, "--simulator-app", simulatorApp, "--device-id", target.DeviceId, "--app-id", target.AppId];
    }

    private JsonObject ValidateRoute(JsonObject response, AudioTarget target)
    {
        var diagnostics = response["diagnostics"] as JsonObject ?? new JsonObject();
        if (response["success"]?.GetValue<bool>() != true)
            throw new AudioInjectionException(response["code"]?.GetValue<string>() ?? "audio-helper-error",
                response["message"]?.GetValue<string>() ?? "The macOS audio helper failed.", diagnostics);
        if (diagnostics["inputRouteVerified"]?.GetValue<bool>() != true
            || diagnostics["audioDeviceUid"]?.GetValue<string>() != deviceUid
            || diagnostics["simulatorDeviceId"]?.GetValue<string>() != target.DeviceId
            || diagnostics["inputRouteEvidence"]?.GetValue<string>() != "simulator-menu-checked-item"
            || diagnostics["systemAudioDefaultsChanged"]?.GetValue<bool>() != false)
            throw new AudioInjectionException("input-route-unverified", "The helper did not verify the exact Simulator and CoreAudio route without changing system defaults.", diagnostics);
        diagnostics["captureVerified"] = false;
        diagnostics["targetMicrophoneVerified"] = false;
        diagnostics["transcriptionVerified"] = false;
        return diagnostics;
    }
}
