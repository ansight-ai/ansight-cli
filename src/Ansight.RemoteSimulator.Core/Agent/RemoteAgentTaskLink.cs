namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentTaskLink(
    string LinkId,
    string? WorkContextId,
    string SessionId,
    string AnnotationBatchId,
    IReadOnlyList<string> AnnotationIds,
    IReadOnlyList<string> FrameIds,
    string Source,
    string Provider,
    string ProviderTaskId,
    string? ProviderTurnId,
    string? ProviderTaskTitle,
    string? WorkingDirectory,
    string SubmittedPrompt,
    string Status,
    string? StatusMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
