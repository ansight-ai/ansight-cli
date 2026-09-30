namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingAnnotation(
    string Id,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    string Label,
    string Source,
    string? Notes,
    string? CaptureGroupId,
    IReadOnlyList<RemoteRecordingAnnotationGeometry> Geometry);
