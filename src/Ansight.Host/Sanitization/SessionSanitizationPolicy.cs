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
