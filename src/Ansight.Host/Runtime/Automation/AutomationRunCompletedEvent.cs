using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

/// <summary>
/// Result of one repository automation execution selected by the C# trigger engine.
/// </summary>
public sealed record AutomationRunCompletedEvent(
    string RunId,
    int AttemptNumber,
    int MaximumAttempts,
    bool WillRetry,
    DateTimeOffset? NextAttemptAtUtc,
    string RepositoryRootPath,
    string AppId,
    string? SessionId,
    string TriggerId,
    string AutomationId,
    string ActionKind,
    string ActionTarget,
    string EventId,
    string CorrelationId,
    AutomationRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int? ExitCode,
    string Message,
    JsonObject? Output,
    string StandardError,
    AutomationEventEnvelope MatchedEvent);
