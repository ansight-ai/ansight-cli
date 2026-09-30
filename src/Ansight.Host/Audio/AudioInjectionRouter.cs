namespace Ansight.Host.Audio;

internal sealed class AudioInjectionRouter
{
    private AudioInjectionEngine? engine;

    public AudioInjectionEngine Engine => engine
        ?? throw new AudioInjectionException("audio-unavailable", "The host audio service is not configured.");

    public void Configure(AudioInjectionEngine value) => engine = value;
}
