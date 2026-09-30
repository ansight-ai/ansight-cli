using System.Text.Json;

namespace Ansight.Cli.Tests.Commands.Update;

public sealed class CliVersionCommandTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("info")]
    public async Task Command_ReportsDataDirectoryAndExecutablePath(string command)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"ansight-info-{Guid.NewGuid():N}");
        var result = await RunAsync([command, "--data-dir", dataDirectory], json: false);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains(
            $"Data: {Path.GetFullPath(dataDirectory)}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Executable: {Environment.ProcessPath ?? "unavailable"}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("— BETA", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task JsonOutput_IncludesDataDirectoryAndExecutablePath()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"ansight-info-{Guid.NewGuid():N}");
        var result = await RunAsync(
            ["info", "--json", "--data-dir", dataDirectory],
            json: true);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(dataDirectory),
            document.RootElement.GetProperty("dataDirectory").GetString());
        Assert.Equal(
            Environment.ProcessPath ?? "unavailable",
            document.RootElement.GetProperty("executablePath").GetString());
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments, bool json)
    {
        var arguments = CliArguments.Parse(commandArguments);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(json, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);
        return new CommandResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}
