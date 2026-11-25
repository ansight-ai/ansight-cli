namespace Ansight.Host.Tests.Unit.Devices;

public sealed class AndroidDisplayViewportTests
{
    [Theory]
    [InlineData(0, 1080, 2424)]
    [InlineData(1, 2424, 1080)]
    [InlineData(2, 1080, 2424)]
    [InlineData(3, 2424, 1080)]
    public void ReadsCurrentLogicalDimensionsInsteadOfNaturalDeviceSize(int rotation, int width, int height)
    {
        var source = $"Viewport INTERNAL: displayId=0, uniqueId=local:4619827259835644672, port=0, orientation={rotation}, logicalFrame=[0, 0, {width}, {height}], physicalFrame=[0, 0, {width}, {height}], deviceSize=[1080, 2424], isActive=[1]";
        Assert.True(AndroidDisplayViewport.TryRead(source, out var size));
        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Viewport INTERNAL: displayId=-1, logicalFrame=[0, 0, 1080, 2424], isActive=[0]")]
    [InlineData("Viewport EXTERNAL: displayId=10, logicalFrame=[0, 0, 1920, 1080], isActive=[1]")]
    [InlineData("Viewport INTERNAL: displayId=0, logicalFrame=[0, 0, 0, 0], isActive=[1]")]
    [InlineData("Viewport INTERNAL: displayId=0, logicalFrame=[0, 0, 1080, 2424], isActive=[0]")]
    [InlineData("Viewport INTERNAL: displayId=0, logicalFrame=[0, 0, 999999999999, 2424], isActive=[1]")]
    public void RejectsUnavailableOrUnrelatedDisplays(string source)
        => Assert.False(AndroidDisplayViewport.TryRead(source, out _));

    [Fact]
    public void IgnoresOtherDisplaysAndAcceptsLegacyDefaultViewportWithoutActiveFlag()
    {
        var source = "Viewport EXTERNAL: displayId=1, logicalFrame=[0, 0, 1920, 1080], isActive=[1]\nViewport INTERNAL: displayId=0, orientation=1, logicalFrame=[0, 0, 2424, 1080]";
        Assert.True(AndroidDisplayViewport.TryRead(source, out var size));
        Assert.Equal(new HeadlessAndroidDisplaySize(2424, 1080), size);
    }
}
