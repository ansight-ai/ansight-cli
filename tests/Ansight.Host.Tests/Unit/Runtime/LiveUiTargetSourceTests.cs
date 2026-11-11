namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiTargetSourceTests
{
    [Theory]
    [InlineData("avalonia.get_visual_tree")]
    [InlineData("avalonia.get_logical_tree")]
    [InlineData("avalonia.get_accessibility_tree")]
    public void AvaloniaProviderHintsAreAcceptedAndAdvertised(string source)
    {
        var schema = LiveUiToolSchemas.TargetSource();

        Assert.True(schema.Nullable);
        Assert.Contains(source, schema.EnumValues);
        Assert.True(LiveUiTreeCapture.IsTargetSource(source));
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("screenshotOcr")]
    [InlineData("frameworkVisualTree")]
    [InlineData("avalonia-visual")]
    [InlineData("arbitrary.tool")]
    public void EvidenceLabelsAndUnknownToolsAreNotTargetProviders(string source)
    {
        Assert.DoesNotContain(source, LiveUiToolSchemas.TargetSource().EnumValues);
        Assert.False(LiveUiTreeCapture.IsTargetSource(source));
    }
}
