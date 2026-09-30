
namespace Ansight.Host.Replay;

public sealed record SessionExplorerShareRequest(
    Guid TeamId,
    string AccessLevel = "team",
    bool IncludeNativeDeviceLogs = false,
    bool Sanitize = false,
    string? SanitizerId = null,
    bool NormalizeSession = true,
    bool EncodeVideo = false,
    bool IncludeNetworkRequests = true,
    bool IncludeArtifacts = true,
    IReadOnlyList<SessionVisualTreeTypeSelection>? IncludedVisualTreeTypes = null);
