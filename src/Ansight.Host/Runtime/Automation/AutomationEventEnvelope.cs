using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Automation;

/// <summary>
/// Normalized runtime event passed to an already-matched repository automation.
/// Trigger matching is performed by the C# host before the selected action is dispatched.
/// </summary>
public sealed class AutomationEventEnvelope
{
    public required string EventId { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string AppId { get; init; }

    public string? SessionId { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public JsonObject Payload { get; init; } = new();
}
