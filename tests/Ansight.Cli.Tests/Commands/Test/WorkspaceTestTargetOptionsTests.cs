namespace Ansight.Cli.Tests.Commands.Test;

public sealed partial class WorkspaceTestTargetOptionsTests
{
    [Fact]
    public void Resolve_ReturnsNullWithoutTargetOptions()
    {
        var arguments = CliArguments.Parse(["test", "run", "/workspace", "test-id"]);

        var result = WorkspaceTestTargetOptions.Resolve(arguments);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_MapsTargetAndApplicationOptions()
    {
        var arguments = CliArguments.Parse(
            [
                "test", "run", "/workspace", "test-id",
                "--platform", "ios",
                "--device-id", "DEVICE-1",
                "--device-kind", "physical",
                "--app", "/tmp/Target.app"
            ]);

        var result = WorkspaceTestTargetOptions.Resolve(arguments);

        Assert.NotNull(result);
        Assert.Equal("ios", result.Platform);
        Assert.Equal("DEVICE-1", result.DeviceIdentifier);
        Assert.Equal("physical", result.DeviceKind);
        Assert.Equal("/tmp/Target.app", result.ApplicationPath);
    }

    [Fact]
    public void Resolve_MapsDeviceAlias()
    {
        var arguments = CliArguments.Parse(
            ["test", "run-all", "/workspace", "--device", "DEVICE-1"]);

        var result = WorkspaceTestTargetOptions.Resolve(arguments);

        Assert.NotNull(result);
        Assert.Equal("DEVICE-1", result.DeviceIdentifier);
    }

    [Fact]
    public void Resolve_RejectsConflictingDeviceOptions()
    {
        var arguments = CliArguments.Parse(
        [
            "test", "run-all", "/workspace",
            "--device", "DEVICE-1",
            "--device-id", "DEVICE-2"
        ]);

        var exception = Assert.Throws<CliUsageException>(() => WorkspaceTestTargetOptions.Resolve(arguments));

        Assert.Contains("only one device identifier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveMany_ExpandsRepeatedDeviceIdentifiersWithSharedTargetOptions()
    {
        var arguments = CliArguments.Parse(
        [
            "test", "run-all", "/workspace",
            "--platform", "ios",
            "--device-id", "DEVICE-1",
            "--device-id", "DEVICE-2",
            "--app", "/tmp/Target.app"
        ]);

        var targets = WorkspaceTestTargetOptions.ResolveMany(arguments);

        Assert.Equal(["DEVICE-1", "DEVICE-2"], targets.Select(static target => target.DeviceIdentifier));
        Assert.All(targets, target =>
        {
            Assert.Equal("ios", target.Platform);
            Assert.Equal("/tmp/Target.app", target.ApplicationPath);
        });
    }

    [Fact]
    public void ResolveMany_MapsHeadlessWithoutOtherTargetOptions()
    {
        var arguments = CliArguments.Parse(
            ["test", "run-all", "/workspace", "--headless"]);

        var targets = WorkspaceTestTargetOptions.ResolveMany(arguments);

        var target = Assert.Single(targets);
        Assert.True(target.Headless);
        Assert.Null(target.Platform);
        Assert.Null(target.DeviceIdentifier);
    }

    [Fact]
    public void ResolveMany_AppliesHeadlessToEverySelectedDevice()
    {
        var arguments = CliArguments.Parse(
        [
            "test", "run-all", "/workspace",
            "--device-id", "DEVICE-1",
            "--device-id", "DEVICE-2",
            "--headless"
        ]);

        var targets = WorkspaceTestTargetOptions.ResolveMany(arguments);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, static target => Assert.True(target.Headless));
    }

    [Fact]
    public void Resolve_RejectsMultipleArtifactPaths()
    {
        var arguments = CliArguments.Parse(
            [
                "test", "run", "/workspace", "test-id",
                "--app", "/tmp/Target.app",
                "--ipa", "/tmp/Target.ipa"
            ]);

        var exception = Assert.Throws<CliUsageException>(() => WorkspaceTestTargetOptions.Resolve(arguments));

        Assert.Contains("only one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_RequiresIpaExtensionForIpaOption()
    {
        var arguments = CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--ipa", "/tmp/Target.app"]);

        var exception = Assert.Throws<CliUsageException>(() => WorkspaceTestTargetOptions.Resolve(arguments));

        Assert.Contains("ending in .ipa", exception.Message, StringComparison.Ordinal);
    }
}
