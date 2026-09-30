namespace Ansight.Cli.Tests.Application;

public sealed class LocalAccessBoundaryTests
{
    [Theory]
    [InlineData("session", "list")]
    [InlineData("session", "show")]
    [InlineData("session", "logs")]
    [InlineData("session", "network")]
    [InlineData("session", "trees")]
    [InlineData("session", "serve")]
    [InlineData("session", "export")]
    [InlineData("session", "import")]
    [InlineData("device", "list")]
    [InlineData("device", "start")]
    [InlineData("device", "screenshot")]
    [InlineData("input", "tap")]
    [InlineData("ui", "snapshot")]
    [InlineData("app", "execute")]
    [InlineData("test", "run")]
    public void LocalToolsNeverRequestCloudAuthorization(string command, string action)
    {
        var arguments = CliArguments.Parse([command, action]);
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(arguments));
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(arguments, runnerConfigured: true));
        Assert.True(CliApplication.ShouldInitializeAnalytics(arguments));
    }

    [Fact]
    public void ResidentHostHasAnAccountFreeLifetime()
    {
        var arguments = CliArguments.Parse(["host", "run"]);
        Assert.Equal(CliAccessClass.ResidentHost, CliCommandAccessPolicy.Classify(arguments));
        Assert.Equal(CliAccessClass.ResidentHost, CliCommandAccessPolicy.Classify(arguments, runnerConfigured: true));
        Assert.True(CliApplication.ShouldInitializeAnalytics(arguments));
    }

    [Theory]
    [InlineData("session", "share")]
    [InlineData("session", "share-batch")]
    [InlineData("session", "summary")]
    [InlineData("session", "summarize")]
    [InlineData("session", "summarise")]
    [InlineData("session", "url")]
    [InlineData("session", "share-url")]
    [InlineData("session", "replay-url")]
    [InlineData("cloud", "build")]
    [InlineData("runner", "submit")]
    public void CloudOperationsRequireThePrivateAuthorizationModule(string command, string action)
        => Assert.Equal(CliAccessClass.Cloud, CliCommandAccessPolicy.Classify(CliArguments.Parse([command, action])));

    [Fact]
    public void RunnerKeyNeverAuthorizesLocalTools()
    {
        Assert.Equal(CliAccessClass.Runner, CliCommandAccessPolicy.Classify(
            CliArguments.Parse(["session", "share"]), runnerConfigured: true));
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(
            CliArguments.Parse(["session", "show"]), runnerConfigured: true));
    }
}
