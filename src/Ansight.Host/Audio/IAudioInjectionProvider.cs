using System.Text.Json.Nodes;

namespace Ansight.Host.Audio;

internal interface IAudioInjectionProvider
{
    string Platform { get; }
    string Backend { get; }
    string GetLeaseKey(AudioTarget target);
    Task<AudioProviderCapabilities> GetCapabilitiesAsync(AudioTarget target, CancellationToken cancellationToken);
    Task<AudioProviderDelivery> InjectAsync(AudioTarget target, AudioFixture fixture, int waitForMicrophoneMs, CancellationToken cancellationToken);
}
