namespace Ansight.Cli.Tests.Commands.Test;

public sealed partial class WorkspaceTestTargetOptionsTests
{
    [Fact]
    public void DeviceModeCreatesTargetAndPropagatesAcrossDevices()
    {
        var target = WorkspaceTestTargetOptions.Resolve(CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--execution-mode", "device"]));
        Assert.Equal("device", target?.ExecutionMode);

        var targets = WorkspaceTestTargetOptions.ResolveMany(CliArguments.Parse(
            ["test", "run-all", "/workspace", "--execution-mode", "device",
                "--device-id", "first", "--device-id", "second", "--headless"]));
        Assert.Equal(2, targets.Count);
        Assert.All(targets, item =>
        {
            Assert.Equal("device", item.ExecutionMode);
            Assert.True(item.Headless);
        });
    }

    [Theory]
    [InlineData("--app-graph", "true")]
    public void DeviceModeRejectsSdkOnlyOptions(string option, string value)
        => Assert.Throws<CliUsageException>(() => WorkspaceTestTargetOptions.Resolve(CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--execution-mode", "device", option, value])));

    [Fact]
    public void DeviceModeAcceptsAnExistingSession()
    {
        var target = WorkspaceTestTargetOptions.Resolve(CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--execution-mode", "device", "--session-id", "session-1"]));
        Assert.Equal("device", target?.ExecutionMode);
    }

    [Fact]
    public void UnknownExecutionModeIsRejected()
        => Assert.Throws<CliUsageException>(() => WorkspaceTestTargetOptions.Resolve(CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--execution-mode", "devcie"])));
}
