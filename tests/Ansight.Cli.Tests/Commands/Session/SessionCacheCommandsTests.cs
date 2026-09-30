using System.Text.Json.Nodes;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Session;

public sealed class SessionCacheCommandsTests
{
    [Fact]
    public async Task Status_ReturnsVersionedEmptyCachePlan()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        var result = await RunAsync(
        [
            "session",
            "cache",
            "status",
            "--json",
            "--data-dir",
            directory.Path,
            "--secret-store-file",
            storePath,
            "--secret-key-file",
            keyPath
        ]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal("ansight.session-cache/v1", payload["schemaVersion"]!.GetValue<string>());
        Assert.Equal("status", payload["operation"]!.GetValue<string>());
        Assert.False(payload["applied"]!.GetValue<bool>());
        Assert.Equal(0, payload["plan"]!["sessionCount"]!.GetValue<int>());
        Assert.Equal(0, payload["plan"]!["deleteCount"]!.GetValue<int>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Prune_WithoutApply_IsDryRun()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        var result = await RunAsync(
        [
            "session",
            "cache",
            "prune",
            "--data-dir",
            directory.Path,
            "--secret-store-file",
            storePath,
            "--secret-key-file",
            keyPath
        ]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("Dry run only", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Compact_UsesDefaultCompactionThreshold()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        var result = await RunAsync(
        [
            "session",
            "cache",
            "compact",
            "--json",
            "--data-dir",
            directory.Path,
            "--secret-store-file",
            storePath,
            "--secret-key-file",
            keyPath
        ]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal(
            "ansight.session-cache-compaction/v1",
            payload["schemaVersion"]!.GetValue<string>());
        Assert.Equal("compact", payload["operation"]!.GetValue<string>());
        Assert.Equal(30, payload["compactionAgeDays"]!.GetValue<int>());
        Assert.Equal(0, payload["compactedSessionCount"]!.GetValue<int>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Compact_AcceptsCustomCompactionThreshold()
    {
        using var directory = TestDirectory.Create();
        var storePath = Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        var result = await RunAsync(
        [
            "session",
            "cache",
            "compact",
            "--compaction-age-days",
            "14",
            "--json",
            "--data-dir",
            directory.Path,
            "--secret-store-file",
            storePath,
            "--secret-key-file",
            keyPath
        ]);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();
        Assert.Equal(14, payload["compactionAgeDays"]!.GetValue<int>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    private static async Task<CommandResult> RunAsync(string[] commandArguments)
    {
        var arguments = CliArguments.Parse(commandArguments);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunParsedAsync(
            arguments,
            new CliOutput(arguments.IsJson, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);
        return new CommandResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}
