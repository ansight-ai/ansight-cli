using Ansight.Cli.Commands.App.Learning;

namespace Ansight.Cli.Tests.Commands.App.Learning;

public sealed class AppLearnCommandTests
{
    [Fact]
    public async Task HelpIsAvailableWithoutAHostOrAccount()
    {
        using var writer = new StringWriter();
        var result = await AppCommands.RunAsync(CliArguments.Parse(["app", "learn", "--help"]),
            new CliOutput(false, writer), CancellationToken.None);
        Assert.Equal(CliExitCodes.Success, result);
        Assert.Contains("--draft-only", writer.ToString());
    }

    [Theory]
    [InlineData("--max-tasks", "0")]
    [InlineData("--max-tasks", "11")]
    [InlineData("--max-actions", "129")]
    [InlineData("--max-actions", "0")]
    [InlineData("--reasoning", "unknown")]
    [InlineData("--focus", "")]
    [InlineData("--workspace", "/unregistered")]
    public void InvalidArgumentsFailBeforeExploration(string option, string value)
        => Assert.Throws<CliUsageException>(() => AppLearnCommand.ValidateArguments(
            CliArguments.Parse(["app", "learn", "com.example.app", option, value])));

    [Fact]
    public void BooleanFlagsCanPrecedeAppIdAndDoNotConsumeIt()
    {
        var arguments = CliArguments.Parse(["app", "learn", "--draft-only", "--launch", "com.example.app"]);
        AppLearnCommand.ValidateArguments(arguments);
        Assert.Equal("com.example.app", arguments.Positionals[2]);
    }

    [Fact]
    public void LearningUsesRequestScopedAiCredentialsAndResidentHost()
    {
        var arguments = CliArguments.Parse(["app", "learn", "com.example.app"]);
        Assert.True(CliCommandAccessPolicy.UsesAiGateway(arguments));
        Assert.True(CliCommandAccessPolicy.UsesRequestScopedRunnerCredentials(arguments));
        Assert.True(ControlClient.ShouldForward(arguments));
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(arguments));
    }

    [Fact]
    public void ExplorationHasBoundedBudgetsAndDoesNotWriteTheLearningReport()
    {
        var arguments = CliArguments.Parse(["app", "learn", "com.example.app", "--reasoning", "deep", "--max-actions", "20",
            "--result-file", "/report.json", "--draft-only", "--session-id", "session-1"]);
        var exploration = AppLearnCommand.BuildExplorationArguments(arguments, "com.example.app");
        AppGraphCommands.ValidateArguments(exploration, "explore", 1);
        Assert.Equal("80", exploration.GetOption("max-round-trips"));
        Assert.Equal("160", exploration.GetOption("max-tool-calls"));
        Assert.Equal("deep", exploration.GetOption("reasoning"));
        Assert.Equal("session-1", exploration.GetOption("session-id"));
        Assert.False(exploration.HasFlag("result-file"));
        Assert.False(exploration.HasFlag("draft-only"));
    }

    [Fact]
    public void PreparationKeepsSessionDataDirectoryAndReasoning()
    {
        var arguments = CliArguments.Parse(["app", "learn", "com.example.app", "--data-dir", "/custom", "--reasoning", "balanced"]);
        var preparation = AppLearnCommand.BuildPreparationArguments(arguments, "session-1");
        Assert.Equal("session-1", preparation.Positionals[2]);
        Assert.Equal("/custom", preparation.GetOption("data-dir"));
        Assert.Equal("balanced", preparation.GetOption("reasoning"));
        Assert.False(preparation.HasFlag("launch"));
    }

    [Fact]
    public void ExistingReportIsPreserved()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "report.json");
        File.WriteAllText(path, "keep");
        Assert.Throws<CliUsageException>(() => AppLearnCommand.ValidateArguments(
            CliArguments.Parse(["app", "learn", "com.example.app", "--result-file", path])));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}
