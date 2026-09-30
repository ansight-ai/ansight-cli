namespace Ansight.Host.Tests.Unit.Utilities;

public sealed class FileNameUtilTests
{
    [Fact]
    public void Sanitize_ReturnsFallbackForBlankValues()
    {
        Assert.Equal("ansight-client", FileNameUtil.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_NormalizesCaseSpacesAndInvalidCharacters()
    {
        var input = " My App/Build ";

        var result = FileNameUtil.Sanitize(input);

        Assert.Equal("my_app_build", result);
    }
}
