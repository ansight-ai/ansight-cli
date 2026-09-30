using System.Text.Json;

namespace Ansight.Cli.Tests.Commands.Config;

public sealed class ConfigCommandsTests
{
    [Fact]
    public void ExplorerAddress_UsesPinnedBuiltInDefaultsUntilTheyAreUnset()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        var settings = new LocalSettingsStore(dataDirectory);

        Assert.Equal("/ansight/", settings.ExplorerPath);
        Assert.Equal(47_231, settings.ExplorerPort);
        Assert.False(settings.HasExplorerPathPreference);
        Assert.False(settings.HasExplorerPortPreference);

        settings.ClearExplorerPath();
        settings.ClearExplorerPort();

        var reloadedSettings = new LocalSettingsStore(dataDirectory);
        Assert.Null(reloadedSettings.ExplorerPath);
        Assert.Null(reloadedSettings.ExplorerPort);
        Assert.True(reloadedSettings.HasExplorerPathPreference);
        Assert.True(reloadedSettings.HasExplorerPortPreference);
    }

    [Fact]
    public void ExplorerPath_CanBeSetReadAndUnset()
    {
        using var directory = TestDirectory.Create();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var dataDirectory = Path.Combine(directory.Path, "host-state");

        var setExitCode = ConfigCommands.Run(
            CliArguments.Parse(
                ["config", "set", "explorer-path", "ansight", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError));

        Assert.Equal(CliExitCodes.Success, setExitCode);
        Assert.Contains("Saved explorer-path: /ansight/", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            "/ansight/",
            new LocalSettingsStore(dataDirectory).ExplorerPath);

        standardOutput.GetStringBuilder().Clear();
        var getExitCode = ConfigCommands.Run(
            CliArguments.Parse(["config", "get", "explorer-path", "--json", "--data-dir", dataDirectory]),
            new CliOutput(true, standardOutput, standardError));
        using var document = JsonDocument.Parse(standardOutput.ToString());

        Assert.Equal(CliExitCodes.Success, getExitCode);
        Assert.Equal("ansight.cli-config/v1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(
            "/ansight/",
            document.RootElement.GetProperty("setting").GetProperty("value").GetString());

        standardOutput.GetStringBuilder().Clear();
        var unsetExitCode = ConfigCommands.Run(
            CliArguments.Parse(["config", "unset", "explorer-path", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError));

        Assert.Equal(CliExitCodes.Success, unsetExitCode);
        Assert.Null(new LocalSettingsStore(dataDirectory).ExplorerPath);
        Assert.Contains("random capability path", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void ExplicitExplorerPathOverridesSavedDefault()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        new LocalSettingsStore(dataDirectory).SetExplorerPath("saved");

        var path = LocalSettingsStore.ResolveExplorerPath(
            CliArguments.Parse(["host", "run", "--serve-path", "explicit"]),
            "serve-path",
            dataDirectory);

        Assert.Equal("explicit", path);
    }

    [Fact]
    public void ExplorerPort_CanBeSetReadAndUnset()
    {
        using var directory = TestDirectory.Create();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var dataDirectory = Path.Combine(directory.Path, "host-state");

        var setExitCode = ConfigCommands.Run(
            CliArguments.Parse(
                ["config", "set", "explorer-port", "47231", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError));

        Assert.Equal(CliExitCodes.Success, setExitCode);
        Assert.Contains("Saved explorer-port: 47231", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(47231, new LocalSettingsStore(dataDirectory).ExplorerPort);

        standardOutput.GetStringBuilder().Clear();
        var getExitCode = ConfigCommands.Run(
            CliArguments.Parse(["config", "get", "explorer-port", "--json", "--data-dir", dataDirectory]),
            new CliOutput(true, standardOutput, standardError));
        using var document = JsonDocument.Parse(standardOutput.ToString());

        Assert.Equal(CliExitCodes.Success, getExitCode);
        Assert.Equal(
            "explorer-port",
            document.RootElement.GetProperty("setting").GetProperty("name").GetString());
        Assert.Equal(
            47231,
            document.RootElement.GetProperty("setting").GetProperty("value").GetInt32());
        Assert.Contains(
            document.RootElement.GetProperty("settings").EnumerateArray(),
            setting => setting.GetProperty("name").GetString() == "explorer-path");

        standardOutput.GetStringBuilder().Clear();
        var unsetExitCode = ConfigCommands.Run(
            CliArguments.Parse(["config", "unset", "explorer-port", "--data-dir", dataDirectory]),
            new CliOutput(false, standardOutput, standardError));

        Assert.Equal(CliExitCodes.Success, unsetExitCode);
        Assert.Null(new LocalSettingsStore(dataDirectory).ExplorerPort);
        Assert.Contains("choose an available port", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void ExplorerPortSavedDefaultCanBeOverriddenIncludingWithAutomaticSelection()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");
        new LocalSettingsStore(dataDirectory).SetExplorerPort("47231");

        var savedPort = LocalSettingsStore.ResolveExplorerPort(
            CliArguments.Parse(["host", "run"]),
            "serve-port",
            dataDirectory);
        var explicitPort = LocalSettingsStore.ResolveExplorerPort(
            CliArguments.Parse(["host", "run", "--serve-port", "47232"]),
            "serve-port",
            dataDirectory);
        var automaticPort = LocalSettingsStore.ResolveExplorerPort(
            CliArguments.Parse(["host", "run", "--serve-port", "0"]),
            "serve-port",
            dataDirectory);

        Assert.Equal(47231, savedPort);
        Assert.Equal(47232, explicitPort);
        Assert.Equal(0, automaticPort);
    }

    [Fact]
    public void InvalidExplorerPathIsRejectedBeforeItIsSaved()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");

        var exception = Assert.Throws<CliUsageException>(() => ConfigCommands.Run(
            CliArguments.Parse(
                ["config", "set", "explorer-path", "nested/path", "--data-dir", dataDirectory]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null)));

        Assert.Contains("one URL-safe segment", exception.Message, StringComparison.Ordinal);
        Assert.Equal("/ansight/", new LocalSettingsStore(dataDirectory).ExplorerPath);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void InvalidExplorerPortIsRejectedBeforeItIsSaved(string port)
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "host-state");

        var exception = Assert.Throws<CliUsageException>(() => ConfigCommands.Run(
            CliArguments.Parse(
                ["config", "set", "explorer-port", port, "--data-dir", dataDirectory]),
            new CliOutput(false, TextWriter.Null, TextWriter.Null)));

        Assert.Contains("between 1 and 65535", exception.Message, StringComparison.Ordinal);
        Assert.Equal(47_231, new LocalSettingsStore(dataDirectory).ExplorerPort);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("settings")]
    public void LocalConfigurationDoesNotRequireAuthentication(string command)
    {
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(
            CliArguments.Parse([command, "list"])));
    }
}
