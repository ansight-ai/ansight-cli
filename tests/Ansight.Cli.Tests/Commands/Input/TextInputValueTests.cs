using Ansight.Cli.Input;

namespace Ansight.Cli.Tests.Commands.Input;

public sealed class TextInputValueTests
{
    [Theory]
    [InlineData("--secret", "login.password")]
    [InlineData("--value", "@secret:login.password")]
    public void AliasFormsResolveWithoutPuttingValuesInArguments(string option, string value)
    {
        var input = TextInputValue.Parse(CliArguments.Parse(["ui", "type", option, value]));
        Assert.Equal("login.password", input.SecretAlias);
        Assert.Null(input.Value);
    }

    [Fact]
    public void QualifiedReferenceCarriesAppAndAliasSeparately()
    {
        var input = TextInputValue.Parse(CliArguments.Parse([
            "ui", "type", "--value", "@secret:com.example.app/login.password"]));
        Assert.Equal("com.example.app", input.AppId);
        Assert.Equal("login.password", input.SecretAlias);
        Assert.Null(input.Value);
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData(" user@example.com ", " user@example.com ")]
    [InlineData("@@secret:login.password", "@secret:login.password")]
    [InlineData("@mention", "@mention")]
    public void LiteralInputIsPreserved(string supplied, string expected)
    {
        var input = TextInputValue.Parse(CliArguments.Parse(["ui", "type", "--value", supplied]));
        Assert.Null(input.SecretAlias);
        Assert.Equal(expected, input.Value);
    }

    [Theory]
    [InlineData("@secret:")]
    [InlineData("@secret:bad alias")]
    [InlineData("@secret:/password")]
    [InlineData("@secret:com.example.app/")]
    [InlineData("@secret:com.example.app/other/password")]
    [InlineData("@secret:bad app/password")]
    public void MalformedReferencesFailInsteadOfBeingTyped(string supplied)
        => Assert.Throws<CliUsageException>(() => TextInputValue.Parse(
            CliArguments.Parse(["ui", "type", "--value", supplied])));

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return new object[] { Array.Empty<string>() };
        yield return new object[] { new[] { "--secret", "one", "--value", "literal" } };
        yield return new object[] { new[] { "--secret", "one", "--secret", "two" } };
        yield return new object[] { new[] { "--value", "one", "--value", "two" } };
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void RequiresOneUnambiguousInputSource(string[] options)
        => Assert.Throws<CliUsageException>(() => TextInputValue.Parse(
            CliArguments.Parse(["ui", "type", .. options])));
}
