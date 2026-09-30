namespace Ansight.Host.Runtime.State;

/// <summary>Validated editable fields; presence flags preserve omitted nullable fields during a patch.</summary>
internal sealed class SessionAnnotationMutation
{
    public required SessionAnnotationMutationKind Kind { get; init; }
    public string? AnnotationId { get; init; }
    public string? ExpectedSource { get; init; }
    public string? Label { get; init; }
    public string? Source { get; init; }
    public bool HasNotes { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? StartUtc { get; init; }
    public bool HasEndUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public IReadOnlyList<SessionAnnotationGeometry>? Geometries { get; init; }
    public bool HasTarget { get; init; }
    public SessionAnnotationTargetReference? Target { get; init; }
}
