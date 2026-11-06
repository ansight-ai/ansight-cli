namespace Ansight.Host.Models.Session;

public sealed class SessionImageFrame
{
    public required string FrameId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Quality { get; init; }
    public required long ByteCount { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Geometry { get; init; }
}
