using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionAnnotationGeometryTests
{
    [Fact]
    public void FreeDraw_PreservesSeparateStrokesAndDerivesBoundsFromTheirPoints()
    {
        var request = Drawing(
            new RemoteAnnotationStroke([new(0.1, 0.2), new(0.3, 0.4), new(0.2, 0.5)]),
            new RemoteAnnotationStroke([new(0.6, 0.8), new(0.9, 0.8)]));

        var result = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Geometry.Count);
        Assert.All(result.Geometry, geometry =>
        {
            Assert.Equal(SessionAnnotationGeometryKind.FreeDraw, geometry.Kind);
            Assert.Equal("frame-1", geometry.FrameId);
        });
        Assert.Equal(3, result.Geometry[0].Points.Count);
        Assert.Equal(2, result.Geometry[1].Points.Count);
        Assert.Equal(0.3, result.Geometry[0].Points[1].X);
        Assert.Equal(0.1, result.Bounds.X);
        Assert.Equal(0.2, result.Bounds.Y);
        Assert.Equal(0.8, result.Bounds.Width, 8);
        Assert.Equal(0.6, result.Bounds.Height, 8);

        var edited = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow, result.Geometry);
        Assert.Equal(result.Geometry[0].GeometryId, edited.Geometry[0].GeometryId);
        Assert.Equal(result.Geometry[1].GeometryId, edited.Geometry[1].GeometryId);
    }

    [Theory]
    [InlineData(double.NaN, 0.2)]
    [InlineData(double.PositiveInfinity, 0.2)]
    [InlineData(-0.1, 0.2)]
    [InlineData(0.2, 1.1)]
    public void FreeDraw_RejectsInvalidCoordinates(double x, double y)
    {
        var result = CliAnnotationGeometry.Create(
            Drawing(new RemoteAnnotationStroke([new(0.1, 0.1), new(x, y)])),
            "frame-1", DateTimeOffset.UtcNow);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Geometry);
    }

    [Fact]
    public void FreeDraw_RejectsMissingDegenerateAndOversizedStrokes()
    {
        var valid = new RemoteAnnotationStroke([new(0.1, 0.1), new(0.2, 0.2)]);
        var requests = new[]
        {
            Drawing(),
            Drawing(new RemoteAnnotationStroke([])),
            Drawing(new RemoteAnnotationStroke([new(0.1, 0.1), new(0.1, 0.1)])),
            Drawing(new RemoteAnnotationStroke(null!)),
            Drawing(null!),
            Drawing([null!]),
            Drawing(Enumerable.Repeat(valid, 17).ToArray()),
            Drawing(new RemoteAnnotationStroke(Enumerable.Repeat(new RemoteAnnotationPoint(0.1, 0.2), 257).ToArray())),
            Drawing(Enumerable.Repeat(new RemoteAnnotationStroke(
                Enumerable.Range(0, 256).Select(index => new RemoteAnnotationPoint(index / 256.0, 0.2)).ToArray()), 5).ToArray())
        };

        foreach (var request in requests)
        {
            var result = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow);
            Assert.NotNull(result.Error);
            Assert.Empty(result.Geometry);
        }
    }

    [Fact]
    public void FreeDraw_PreservesTapDotsAlongsideStrokes()
    {
        var request = Drawing(
            new RemoteAnnotationStroke([new(0.4, 0.5)]),
            new RemoteAnnotationStroke([new(0.2, 0.3), new(0.8, 0.7)]));

        var result = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Geometry.Count);
        Assert.Equal(SessionAnnotationGeometryKind.Point, result.Geometry[0].Kind);
        Assert.Equal(0.4, result.Geometry[0].X);
        Assert.Equal(0.5, result.Geometry[0].Y);
        Assert.Equal(SessionAnnotationGeometryKind.FreeDraw, result.Geometry[1].Kind);

        var dotOnly = CliAnnotationGeometry.Create(
            Drawing(new RemoteAnnotationStroke([new(1, 0)])), "frame-1", DateTimeOffset.UtcNow);

        Assert.Null(dotOnly.Error);
        Assert.Equal(SessionAnnotationGeometryKind.Point, Assert.Single(dotOnly.Geometry).Kind);
        Assert.InRange(1.0, dotOnly.Bounds.X, dotOnly.Bounds.X + dotOnly.Bounds.Width);
        Assert.InRange(0.0, dotOnly.Bounds.Y, dotOnly.Bounds.Y + dotOnly.Bounds.Height);
    }

    [Fact]
    public void Point_PreservesTapCoordinatesInsideItsTargetBounds()
    {
        var request = new RemoteAnnotationRequest("device", "point", 0.5, 0.6, null, null, "Note", "investigate");
        var result = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow);

        Assert.Null(result.Error);
        var point = Assert.Single(result.Geometry);
        Assert.Equal(0.5, point.X);
        Assert.Equal(0.6, point.Y);
        Assert.InRange(point.X, result.Bounds.X, result.Bounds.X + result.Bounds.Width);
        Assert.InRange(point.Y, result.Bounds.Y, result.Bounds.Y + result.Bounds.Height);
    }

    [Fact]
    public void Rectangle_PreservesRegionAndRejectsUnknownKind()
    {
        var request = new RemoteAnnotationRequest("device", "rectangle", 0.1, 0.2, 0.3, 0.4, "Note", "investigate");
        var result = CliAnnotationGeometry.Create(request, "frame-1", DateTimeOffset.UtcNow);

        Assert.Null(result.Error);
        var region = Assert.Single(result.Geometry);
        Assert.Equal(SessionAnnotationGeometryKind.Rectangle, region.Kind);
        Assert.Equal(0.3, region.Width);
        Assert.Equal(0.4, region.Height);
        Assert.NotNull(CliAnnotationGeometry.Create(request with { Kind = "unknown" }, "frame-1", DateTimeOffset.UtcNow).Error);
    }

    private static RemoteAnnotationRequest Drawing(params RemoteAnnotationStroke[] strokes)
    {
        return new RemoteAnnotationRequest("device", "freeDraw", 0, 0, 1, 1,
            "Drawing note", "investigate", "batch-1", "frame-1", strokes);
    }
}
