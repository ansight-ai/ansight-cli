namespace Ansight.Host.Sanitization;

internal sealed class SessionArtifactSanitizationPolicy
{
    public SessionArtifactSanitizationMode Mode { get; init; } = SessionArtifactSanitizationMode.SanitizeTextAndExcludeBinary;

    public long MaximumTextBytes { get; init; } = 8 * 1024 * 1024;
}
