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

    [Theory]
    [InlineData("summary")]
    [InlineData("summarize")]
    [InlineData("summarise")]
    [InlineData("summary-local")]
    [InlineData("summarize-local")]
    [InlineData("summarise-local")]
    public void SummaryAliasesAcceptRunnerCredentials(string action)
    {
        foreach (var command in new[] { "session", "sessions" })
        {
            var arguments = CliArguments.Parse([command, action, "capture", "--local"]);
            Assert.Equal(CliAccessClass.Runner, CliCommandAccessPolicy.Classify(arguments, true));
            Assert.Equal(CliAccessClass.Cloud, CliCommandAccessPolicy.Classify(arguments));
        }
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
    [InlineData("session", "summary-local")]
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

    [Theory]
    [InlineData(true, "session", "summary")]
    [InlineData(true, "sessions", "summarise-local")]
    [InlineData(true, "session", "share")]
    [InlineData(true, "cloud", "analysis", "run")]
    [InlineData(true, "test", "run")]
    [InlineData(false, "session", "show")]
    [InlineData(false, "cloud", "key", "issue")]
    [InlineData(false, "cloud", "team", "list")]
    public void OnlySupportedOperationsReceiveRequestScopedRunnerCredentials(bool expected, params string[] command)
        => Assert.Equal(expected, CliCommandAccessPolicy.UsesRequestScopedRunnerCredentials(CliArguments.Parse(command)));

    [Fact]
    public void ForwardedCredentialsClassifySummaryAsRunnerOnlyForTheirRequest()
    {
        var prior = Ansight.Host.Workspaces.Execution.RunnerAiCredentialContext.Current;
        using (Ansight.Host.Workspaces.Execution.RunnerAiCredentialContext.Push(new("request-key", Guid.NewGuid().ToString())))
        {
            Assert.True(CliCommandAccessPolicy.HasRunnerCredentials);
            Assert.Equal(CliAccessClass.Runner, CliCommandAccessPolicy.Classify(
                CliArguments.Parse(["session", "summary", "capture", "--local"]), CliCommandAccessPolicy.HasRunnerCredentials));
        }
        Assert.Same(prior, Ansight.Host.Workspaces.Execution.RunnerAiCredentialContext.Current);
    }
}
