using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.LocalSimulator;

internal sealed record AnnotationGeometryResult(
    string? Error,
    SessionAnnotationTargetBounds Bounds,
    IReadOnlyList<SessionAnnotationGeometry> Geometry);
