using Ansight.Cli.Commands.Mcp;
using Ansight.Host;
using Ansight.Infrastructure.Security;
using System.Text.Json;

namespace Ansight.Cli.Tests.Commands.Mcp;

public sealed class ReplayHostConnectionTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("images")]
    [InlineData("annotations")]
    [InlineData("touches")]
    public async Task ReplayReadsNeverCompeteForTheDataLockWhileHostDiscoveryIsUnavailable(string operation)
    {
        using var directory = TestDirectory.Create();
        using var dataLock = CliDataDirectoryLock.Acquire(directory.Path);
        string[] command = operation == "list" ? ["session", operation] : ["session", operation, "capture-1"];

        var error = await Assert.ThrowsAsync<CliHostUnavailableException>(() =>
            ReplayMcpCommand.RunCommandAsync(command, directory.Path, CancellationToken.None));

        Assert.Contains("host", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ansight.lock", error.Message);
        Assert.Equal([Path.Combine(directory.Path, "ansight.lock")], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task StoppedHostStatusGivesAnActionableErrorWithoutOpeningStorage()
    {
        using var directory = TestDirectory.Create();
        var error = await Assert.ThrowsAsync<CliHostUnavailableException>(() =>
            ReplayMcpCommand.RunCommandAsync(["host", "status"], directory.Path, CancellationToken.None));

        Assert.Contains("ansight host run", error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrStaleHostNeverCreatesAStandaloneRuntime(bool stale)
    {
        using var directory = TestDirectory.Create();
        if (stale)
        {
            var metadata = new CliHostMetadata(int.MaxValue, null, true, "stale-pipe", directory.Path, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(CliRuntime.ResolveMetadataPath(directory.Path), JsonSerializer.Serialize(metadata));
        }
        var before = Directory.GetFileSystemEntries(directory.Path);

        await Assert.ThrowsAsync<CliHostUnavailableException>(() =>
            ReplayMcpCommand.RunCommandAsync(["session", "list"], directory.Path, CancellationToken.None));

        Assert.Equal(before, Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact(Timeout = 15000)]
    public async Task ConcurrentReadsUseTheHostLockAndRediscoverItAfterRestart()
    {
        using var directory = TestDirectory.Create();
        using var dataLock = CliDataDirectoryLock.Acquire(directory.Path);
        var key = Path.Combine(directory.Path, "test-key");
        FileEncryptionKeyProvider.CreateKeyFile(key);
        await using var runtime = new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = directory.Path,
            SecureStorageFilePath = Path.Combine(directory.Path, "test-store.json"),
            SecureStorageKeyFilePath = key
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        for (var generation = 0; generation < 2; generation++)
        {
            // An MCP server can outlive the host and retry through a discovery gap.
            await Assert.ThrowsAsync<CliHostUnavailableException>(() =>
                ReplayMcpCommand.RunCommandAsync(["session", "list"], directory.Path, timeout.Token));
            await using (var server = new ControlServer(runtime, directory.Path, timeout.Token, CliLocalAccessAuthorizer.Instance))
            {
                server.Start();
                var url = new Uri($"http://127.0.0.1:{48000 + generation}/ansight/");
                await CliRuntime.WriteRecoveryMetadataAsync(directory.Path, url, null, timeout.Token, server.PipeName);

                var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                    ReplayMcpCommand.RunCommandAsync(["session", "list"], directory.Path, timeout.Token)));
                Assert.All(results, result => Assert.Empty(result["sessions"]!.AsArray()));
                var status = await ReplayMcpCommand.RunCommandAsync(["host", "status"], directory.Path, timeout.Token);
                Assert.Equal(url.AbsoluteUri, status["explorerUrl"]!.GetValue<string>());
                Assert.Throws<CliHostUnavailableException>(() => CliDataDirectoryLock.Acquire(directory.Path));
            }
            CliRuntime.TryDeleteMetadata(directory.Path);
        }
    }
}
