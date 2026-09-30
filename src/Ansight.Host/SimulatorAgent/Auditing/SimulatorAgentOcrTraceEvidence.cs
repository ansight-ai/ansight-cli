namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentOcrTraceEvidence(
    string? Provider,
    bool Available,
    string? Message,
    DateTimeOffset CapturedAtUtc,
    string? ScreenshotFrameId,
    string? ScreenshotSha256,
    string? ScreenshotFormat,
    int ScreenWidth,
    int ScreenHeight,
    int DetectionCount,
    SimulatorAgentAuditPayload Results)
{
    public string? ScreenshotPath { get; init; }

    public string? ResultsPath { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal string? SourceScreenshotPath { get; init; }
}
