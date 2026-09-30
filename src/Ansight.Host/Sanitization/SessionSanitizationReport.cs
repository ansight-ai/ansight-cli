namespace Ansight.Host.Sanitization;

public sealed class SessionSanitizationReport
{
    public string Schema { get; init; } = "ansight.session-sanitization-report.v1";

    public required string PolicyId { get; init; }

    public required DateTimeOffset SanitizedAtUtc { get; init; }

    public int StringsScanned { get; init; }

    public int RedactedStrings { get; init; }

    public int SensitivePropertiesRedacted { get; init; }

    public int ScreenshotsRedacted { get; init; }

    public int ScreenshotsRemoved { get; init; }

    public int ScreenshotRegionsRedacted { get; init; }

    public int ArtifactsSanitized { get; init; }

    public int ArtifactsExcluded { get; init; }

    public IReadOnlyDictionary<string, int> RedactionsByRule { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
