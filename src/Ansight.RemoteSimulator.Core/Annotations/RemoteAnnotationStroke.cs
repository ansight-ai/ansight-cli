namespace Ansight.RemoteSimulator.Core.Annotations;

public sealed record RemoteAnnotationPoint(double X, double Y);

public sealed record RemoteAnnotationStroke(IReadOnlyList<RemoteAnnotationPoint> Points);
