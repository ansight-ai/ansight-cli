using Ansight.Host.Runtime.Operations.Tools.TouchReview;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class PencilFrameworkTouchReviewTests
{
    [Theory]
    [InlineData("hoverEnter", "hoverenter")]
    [InlineData("hoverMove", "hovermove")]
    [InlineData("hoverExit", "hoverexit")]
    public void ReviewPreservesPencilProximityActions(string input, string expected)
        => Assert.Equal(expected, TouchReviewGeometry.NormalizeTouchAction(input));
}
