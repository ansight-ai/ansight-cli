using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Serve;

public sealed class SessionServeCommandsTests
{
    private static readonly JsonSerializerOptions protocolJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TopLevelServeHelpDescribesExplorerAndLocationFeatures()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["serve", "--help"]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("visual-tree inspection", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("GPX/KML route replay", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--path <path>", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task TopLevelServeStopExplainsThatTheExplorerFollowsTheHostLifetime()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["serve", "stop"]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("always serves", standardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("ansight host stop", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task ServeReturnsResidentReplayUrlAndStopEndsServer()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var archivePath = Path.Combine(directory.Path, "serve-session.zip");
        await WriteArchiveAsync(archivePath);
        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        var session = Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);

        var serveOutput = await RunAsync(
            runtime,
            directory.Path,
            ["session", "serve", session.SessionId, "--json", "--data-dir", directory.Path]);
        var servePayload = JsonNode.Parse(serveOutput)!.AsObject();
        Assert.Equal("ansight.session-local-replay/v1", servePayload["schema"]!.GetValue<string>());
        Assert.True(servePayload["isSuccess"]!.GetValue<bool>());
        Assert.True(servePayload["isResidentHost"]!.GetValue<bool>());
        var replayUrl = new Uri(servePayload["replayUrl"]!.GetValue<string>());
        using var httpClient = new HttpClient();
        Assert.Contains("Ansight Local Replay", await httpClient.GetStringAsync(replayUrl), StringComparison.Ordinal);

        var stopOutput = await RunAsync(
            runtime,
            directory.Path,
            ["session", "serve", "stop", session.SessionId, "--json", "--data-dir", directory.Path]);
        var stopPayload = JsonNode.Parse(stopOutput)!.AsObject();
        Assert.True(stopPayload["isSuccess"]!.GetValue<bool>());
    }

    [Fact(Timeout = 30000)]
    public async Task ShowAcceptsExactlyOneSessionIdentifier()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var archivePath = Path.Combine(directory.Path, "show-session.zip");
        await WriteArchiveAsync(archivePath);
        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        var session = Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);

        var showOutput = await RunAsync(
            runtime,
            directory.Path,
            ["session", "show", session.SessionId, "--json", "--data-dir", directory.Path]);
        var showPayload = JsonNode.Parse(showOutput)!.AsObject();

        Assert.Equal("ansight.session/v1", showPayload["schema"]!.GetValue<string>());
        Assert.Equal(session.SessionId, showPayload["session"]!["sessionId"]!.GetValue<string>());
    }

    private static RuntimeCoordinator CreateRuntime(string baseFolderPath)
    {
        var storagePath = Path.Combine(baseFolderPath, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = baseFolderPath,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
    }

    private static async Task<string> RunAsync(
        RuntimeCoordinator runtime,
        string dataDirectory,
        IReadOnlyList<string> arguments)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        using var context = CliCommandContext.Push(runtime, dataDirectory, secretValue: null);
        var exitCode = await CliApplication.RunParsedAsync(
            CliArguments.Parse(arguments),
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None,
            accessAuthorizer: TestAccessAuthorizer.Allow,
            allowResidentHostForwarding: false);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        return standardOutput.ToString();
    }

    private static async Task WriteArchiveAsync(string archivePath)
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-17T02:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = $"cli-replay-{Guid.NewGuid():N}",
            AppId = "com.example.cli-replay",
            ClientName = "CLI Replay",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(1),
            IsHistorical = true,
            MetricChannels = [],
            Metrics = []
        };
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var sessionEntry = archive.CreateEntry("session.json");
        await using var sessionStream = sessionEntry.Open();
        await JsonSerializer.SerializeAsync(
            sessionStream,
            new SessionCaptureDocument
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Session = snapshot
            },
            protocolJson);
    }
}
