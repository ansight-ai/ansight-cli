namespace Ansight.Host.Replay;

public sealed record SessionExplorerEnrollmentInviteRequest(
    string? AppId = null,
    string? AppName = null,
    string? Duration = null,
    string? HostAddress = null);
