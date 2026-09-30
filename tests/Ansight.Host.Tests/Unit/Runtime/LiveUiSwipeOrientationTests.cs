using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiSwipeOrientationTests
{
    [Theory]
    [InlineData("E to W", -1, 0)]
    [InlineData("E→W", -1, 0)]
    [InlineData("S to N", 0, -1)]
    [InlineData("north-east", 0.7071067811865475, -0.7071067811865475)]
    [InlineData("NE to SW", -0.7071067811865475, 0.7071067811865475)]
    [InlineData("up", 0, -1)]
    public void TryParse_ResolvesCompassDirectionsAndPaths(
        string value,
        double expectedX,
        double expectedY)
    {
        var parsed = LiveUiSwipeOrientation.TryParse(value, out var vector);

        Assert.True(parsed);
        Assert.Equal(expectedX, vector.X, precision: 10);
        Assert.Equal(expectedY, vector.Y, precision: 10);
    }

    [Theory]
    [InlineData("E to E")]
    [InlineData("clockwise")]
    public void TryParse_RejectsAmbiguousOrientations(string value)
    {
        Assert.False(LiveUiSwipeOrientation.TryParse(value, out _));
    }
}
