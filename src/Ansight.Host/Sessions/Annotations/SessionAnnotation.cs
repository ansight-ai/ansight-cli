namespace Ansight.Host.Models.Session;

using System.Text.Json.Nodes;

public sealed class SessionAnnotation
{
    public required string AnnotationId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset? EndUtc { get; init; }

    public required string Label { get; init; }

    public string Source { get; init; } = "ansight";

    public string? Notes { get; init; }

    public string? CaptureGroupId { get; init; }

    public JsonObject? CustomData { get; init; }

    public IReadOnlyList<SessionAnnotationEvidence> Evidence { get; init; } = Array.Empty<SessionAnnotationEvidence>();

    public IReadOnlyList<string> HookFailures { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SessionAnnotationGeometry> Geometry { get; init; } = Array.Empty<SessionAnnotationGeometry>();

    public SessionAnnotationTarget? Target { get; init; }
}
