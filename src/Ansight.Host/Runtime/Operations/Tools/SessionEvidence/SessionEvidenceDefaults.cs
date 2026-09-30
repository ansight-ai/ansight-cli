namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class SessionEvidenceDefaults
{
    public const int DefaultResultLimit = 200;
    public const int MaxResultLimit = 5000;
    public const int DefaultTimelineLimit = 1000;
    public const int MaxTimelineLimit = 10000;
    public const int DefaultBucketCount = 24;
    public const int MaxBucketCount = 200;
    public const int DefaultArtifactReadBytes = 64 * 1024;
    public const int MaxArtifactReadBytes = 1024 * 1024;
    public const int DefaultVisualTreeSearchLimit = 100;
    public const int MaxVisualTreeSearchLimit = 1000;
    public const int DefaultVisualTreeMaxDepth = 32;
    public const int DefaultScreenshotQuality = 80;
    public const int DefaultScreenshotMaxWidth = 1280;
    public const int DefaultAuditLogLimit = 100;
    public const int MaxAuditLogLimit = 1000;
    public const string SessionExportsDirectoryName = "session-exports";
}
