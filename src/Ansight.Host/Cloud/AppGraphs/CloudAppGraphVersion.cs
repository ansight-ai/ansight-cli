using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed class CloudAppGraphVersion
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("app_graph_id")]
    public Guid AppGraphId { get; init; }

    [JsonPropertyName("version_number")]
    public int VersionNumber { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "draft";

    [JsonPropertyName("definition")]
    public JsonObject Definition { get; init; } = new();

    [JsonPropertyName("merge_summary")]
    public string? MergeSummary { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}
