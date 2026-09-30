using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationExecutionResult(
    AutomationRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int? ExitCode,
    string Message,
    JsonObject? Output,
    string StandardError);
