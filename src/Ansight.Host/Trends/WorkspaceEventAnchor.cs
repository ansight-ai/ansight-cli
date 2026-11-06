namespace Ansight.Host.Trends;

public sealed record WorkspaceEventAnchor(
    string Label,
    string? EventType = null,
    byte? ChannelId = null)
{
    // Keep hashes of existing event-only definitions stable.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceTrendsLogSelector? Log { get; init; }
}
