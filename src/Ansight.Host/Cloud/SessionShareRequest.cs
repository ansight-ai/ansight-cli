namespace Ansight.Host.Cloud;

public sealed record SessionShareRequest(
    string SessionId,
    Guid? TeamId = null,
    string? TeamName = null,
    string AccessLevel = "team",
    bool IncludeNativeDeviceLogs = false,
    bool Sanitize = false,
    string? SanitizerModulePath = null,
    bool EncodeVideo = false,
    bool NormalizeSession = true,
    string? SanitizerId = null,
    bool IncludeNetworkRequests = true,
    bool IncludeArtifacts = true,
    IReadOnlyList<SessionVisualTreeTypeSelection>? IncludedVisualTreeTypes = null)
{
    public bool DeferUploadNotification { get; init; }
}
