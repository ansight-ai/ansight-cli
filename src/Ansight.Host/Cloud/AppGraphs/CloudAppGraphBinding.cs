using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Cloud;

public sealed class CloudAppGraphBinding
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("team_id")]
    public Guid TeamId { get; init; }

    [JsonPropertyName("app_graph_version_id")]
    public Guid AppGraphVersionId { get; init; }

    [JsonPropertyName("edge_id")]
    public string EdgeId { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("mechanism")]
    public string Mechanism { get; init; } = "app_tool";

    [JsonPropertyName("configuration")]
    public JsonObject Configuration { get; init; } = new();

    [JsonPropertyName("preconditions")]
    public string[] Preconditions { get; init; } = [];

    [JsonPropertyName("postconditions")]
    public string[] Postconditions { get; init; } = [];

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }
}
