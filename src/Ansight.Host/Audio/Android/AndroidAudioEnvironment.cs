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

internal sealed class AndroidAudioEnvironment(RuntimeOptions options) : IAndroidAudioEnvironment
{
    private readonly AndroidAudioEndpointResolver resolver = new();

    public AndroidAudioEndpoint ResolveEndpoint(string serial) => resolver.Resolve(serial);
    public bool IsEndpointCurrent(AndroidAudioEndpoint endpoint) => resolver.IsCurrent(endpoint);
    public IAndroidEmulatorAudioClient CreateClient(AndroidAudioEndpoint endpoint) => new AndroidEmulatorAudioClient(endpoint);

    public async Task<AndroidMicrophoneObservation> ReadMicrophoneAsync(string serial, CancellationToken cancellationToken)
    {
        var resolution = AdbToolLocator.Resolve(options.AdbPath);
        if (!resolution.IsFound) { throw new AudioInjectionException("adb-unavailable", "ADB is required to verify microphone readiness on the target emulator."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var result = await new AdbClient(resolution.AdbPath).RunAsync(
                ["-s", serial, "shell", "dumpsys", "media.audio_flinger"], timeout.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new AudioInjectionException("microphone-state-unknown", "ADB could not read the target emulator's microphone state; no audio was injected.");
            }
            return AndroidMicrophoneParser.Parse(result.StandardOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AudioInjectionException("microphone-state-unknown", "Reading the emulator microphone state timed out; no audio was injected.");
        }
    }
}
