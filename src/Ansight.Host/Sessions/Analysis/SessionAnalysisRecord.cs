namespace Ansight.Host.Models.Session;

public sealed class SessionAnalysisRecord
{
    public required string AnalysisId { get; init; }
    public required string AgentId { get; init; }
    public string AnalysisKind { get; init; } = SessionAnalysisKind.General;
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public required bool Success { get; init; }
    public string? StatusMessage { get; init; }
    public string? Prompt { get; init; }
    public string? Transcript { get; init; }
    public string? FinalResponse { get; init; }
    public string? MermaidDefinition { get; init; }
}
