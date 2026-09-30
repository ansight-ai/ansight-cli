namespace Ansight.Host.Sanitization;

internal sealed class SessionSanitizationPolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Id { get; init; } = "pii-default";

    public string Replacement { get; init; } = "[REDACTED]";

    public IReadOnlyList<string> Detectors { get; init; } =
    [
        "email",
        "phone",
        "ipAddress",
        "creditCard",
        "credential"
    ];

    public IReadOnlyList<string> SensitiveProperties { get; init; } =
    [
        "address",
        "author",
        "authorization",
        "birthDate",
        "clientName",
        "cookie",
        "dateOfBirth",
        "email",
        "firstName",
        "fullName",
        "lastName",
        "password",
        "phone",
        "secret",
        "token",
        "userName"
    ];

    public IReadOnlyList<SessionSanitizationRegexRule> Rules { get; init; } = [];

    public SessionScreenshotSanitizationPolicy Screenshots { get; init; } = new();

    public SessionArtifactSanitizationPolicy Artifacts { get; init; } = new();

    public static SessionSanitizationPolicy PiiSafeDefault { get; } = new();
}

internal sealed class SessionSanitizationRegexRule
{
    public required string Id { get; init; }

    public required string Pattern { get; init; }

    public string? Replacement { get; init; }

    public bool IgnoreCase { get; init; } = true;
}

internal sealed class SessionScreenshotSanitizationPolicy
{
    public SessionScreenshotSanitizationMode Mode { get; init; } = SessionScreenshotSanitizationMode.SensitiveRegions;

    public SessionScreenshotSanitizationFallback Fallback { get; init; } = SessionScreenshotSanitizationFallback.RedactAll;

    public int PaddingPixels { get; init; } = 6;
}

internal enum SessionScreenshotSanitizationMode
{
    Keep,
    SensitiveRegions,
    RedactAll,
    Remove
}

internal enum SessionScreenshotSanitizationFallback
{
    Keep,
    RedactAll,
    Remove
}

internal sealed class SessionArtifactSanitizationPolicy
{
    public SessionArtifactSanitizationMode Mode { get; init; } = SessionArtifactSanitizationMode.SanitizeTextAndExcludeBinary;

    public long MaximumTextBytes { get; init; } = 8 * 1024 * 1024;
}

internal enum SessionArtifactSanitizationMode
{
    Keep,
    Exclude,
    SanitizeTextAndExcludeBinary
}
