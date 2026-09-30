namespace Ansight.Host.Cloud;

public sealed record CloudSessionTeam(
    Guid TeamId,
    string Name,
    int MemberCap,
    string? Role,
    CloudSessionAttachmentLimits AttachmentLimits)
{
    public IReadOnlyList<string> AllowedAppIds { get; init; } = ["*"];

    public bool AllowsAppId(string? appId)
    {
        if (AllowedAppIds.Count == 0
            || AllowedAppIds.Any(static candidate => string.Equals(candidate, "*", StringComparison.Ordinal)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(appId)
               && AllowedAppIds.Any(candidate =>
                   string.Equals(candidate, appId.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
