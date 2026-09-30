using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.Cloud;

internal sealed class WorkspaceTestTeamMembership
{
    [JsonPropertyName("id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("allowed_app_ids")]
    public string[] AllowedAppIds { get; init; } = ["*"];

    public bool AllowsAppId(string appId) =>
        AllowedAppIds.Length == 0
        || AllowedAppIds.Any(candidate => string.Equals(candidate, "*", StringComparison.Ordinal))
        || AllowedAppIds.Any(candidate => string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase));
}
