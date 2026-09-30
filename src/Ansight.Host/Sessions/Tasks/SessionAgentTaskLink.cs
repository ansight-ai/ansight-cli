namespace Ansight.Host.Models.Session;

public sealed class SessionAgentTaskLink
{
    public required string LinkId { get; init; }

    public string? WorkContextId { get; init; }

    public required string SessionId { get; init; }

    public required string AnnotationBatchId { get; init; }

    public IReadOnlyList<string> AnnotationIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FrameIds { get; init; } = Array.Empty<string>();

    public required string Source { get; init; }

    public required string Provider { get; init; }

    public required string ProviderTaskId { get; init; }

    public string? ProviderTurnId { get; init; }

    public string? ProviderTaskTitle { get; init; }

    public string? WorkingDirectory { get; init; }

    public required string SubmittedPrompt { get; init; }

    public required string Status { get; init; }

    public string? StatusMessage { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
