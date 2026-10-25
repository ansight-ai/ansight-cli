namespace Ansight.Cli.Tests.Commands;

public sealed class RunVideoOptionsTests
{
    [Theory]
    [InlineData("app", "execute")]
    [InlineData("test", "run")]
    [InlineData("test", "run-all")]
    public void NoVideoFlagDoesNotConsumePositionals(string command, string action)
    {
        var arguments = CliArguments.Parse([command, action, "--no-video", "target"]);
        Assert.Equal("target", arguments.RequirePositional(2, "target"));
        Assert.Equal("off", RunVideoOptions.Resolve(arguments)?.Retention);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("on-failure")]
    [InlineData("off")]
    public void RetentionCanOverrideSavedConfiguration(string retention)
    {
        Assert.Equal(retention, RunVideoOptions.Resolve(CliArguments.Parse(
            ["app", "execute", "session", "--video-retention", retention]))?.Retention);
        Assert.Equal(retention, RunVideoOptions.Resolve(CliArguments.Parse(
            ["test", "run", "workspace", "test", "--video-retention", retention]))?.Retention);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("on-failure")]
    public void NoVideoRejectsConflictingRetention(string retention)
        => Assert.Throws<CliUsageException>(() => RunVideoOptions.Resolve(CliArguments.Parse(
            ["app", "execute", "session", "--no-video", "--video-retention", retention])));

    [Fact]
    public void AbsentFlagsPreserveSavedConfiguration()
        => Assert.Null(RunVideoOptions.Resolve(CliArguments.Parse(["test", "run", "workspace", "test"])));

    [Theory]
    [InlineData("--video-retention")]
    [InlineData("--video-retention=never")]
    [InlineData("--video")]
    [InlineData("--video=false")]
    [InlineData("--no-video=false")]
    [InlineData("--no-video=")]
    [InlineData("--video=")]
    public void InvalidOptionsFailClearly(string flag)
        => Assert.Throws<CliUsageException>(() => RunVideoOptions.Resolve(
            CliArguments.Parse(["app", "execute", "session", flag])));
}
