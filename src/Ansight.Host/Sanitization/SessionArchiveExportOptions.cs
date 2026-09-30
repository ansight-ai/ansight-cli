namespace Ansight.Host.Sanitization;

public sealed class SessionArchiveExportOptions
{
    public bool IncludeNativeDeviceLogs { get; init; } = true;

    public bool IncludeNetworkRequests { get; init; } = true;

    public bool IncludeArtifacts { get; init; } = true;

    public IReadOnlyList<SessionVisualTreeTypeSelection>? IncludedVisualTreeTypes { get; init; }

    public bool Sanitize { get; init; }

    public string? SanitizerModulePath { get; init; }

    public SessionSanitizerOperationContext SanitizationContext { get; init; } = SessionSanitizerOperationContext.Export;

    public bool IncludeSanitizationReport { get; init; } = true;
}
