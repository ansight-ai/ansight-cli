using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed class CloudAppGraphSummary
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("team_app_id")]
    public Guid TeamAppId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string Intent { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "draft";

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("current_version_id")]
    public Guid? CurrentVersionId { get; init; }

    [JsonPropertyName("published_version_id")]
    public Guid? PublishedVersionId { get; init; }

    [JsonPropertyName("observation_count")]
    public int ObservationCount { get; init; }

    [JsonPropertyName("successful_run_count")]
    public int SuccessfulRunCount { get; init; }

    [JsonPropertyName("failed_run_count")]
    public int FailedRunCount { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}
