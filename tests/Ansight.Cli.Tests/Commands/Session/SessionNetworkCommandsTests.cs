using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Session;

public sealed class SessionNetworkCommandsTests
{
    private static readonly JsonSerializerOptions protocolJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Network_FiltersRequestsAndReturnsStructuredEvidence()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportNetworkSessionAsync(runtime, directory.Path);

        var result = await RunAsync(
            runtime,
            directory.Path,
            [
                "session", "network", session.SessionId,
                "--method", "GET",
                "--status", "2xx",
                "--host", "api.example.test",
                "--json",
                "--data-dir", directory.Path
            ]);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal("ansight.session-network/v1", payload["schema"]!.GetValue<string>());
        Assert.Equal(2, payload["totalRequestCount"]!.GetValue<int>());
        Assert.Equal(1, payload["matchedRequestCount"]!.GetValue<int>());
        var request = Assert.Single(payload["requests"]!.AsArray())!.AsObject();
        Assert.Equal("request-success", request["id"]!.GetValue<string>());
        Assert.Equal(200, request["statusCode"]!.GetValue<int>());
        Assert.Contains("token=%3Credacted%3E", request["url"]!.GetValue<string>(), StringComparison.Ordinal);
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

    private static async Task<CommandResult> RunAsync(
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
        return new CommandResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task<AppSessionSnapshot> ImportNetworkSessionAsync(
        RuntimeCoordinator runtime,
        string directoryPath)
    {
        var archivePath = Path.Combine(directoryPath, "network-session.zip");
        var createdUtc = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = $"cli-network-{Guid.NewGuid():N}",
            AppId = "com.example.cli-network",
            ClientName = "CLI Network",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = true,
            MetricChannels = [],
            Metrics = []
        };
        var requests = new[]
        {
            new SessionNetworkRequest
            {
                Id = "request-success",
                Source = "test",
                StartedAtUtc = createdUtc,
                CompletedAtUtc = createdUtc.AddMilliseconds(80),
                DurationMilliseconds = 80,
                Method = "GET",
                Url = "https://api.example.test/orders?token=secret",
                StatusCode = 200
            },
            new SessionNetworkRequest
            {
                Id = "request-failed",
                Source = "test",
                StartedAtUtc = createdUtc.AddSeconds(1),
                CompletedAtUtc = createdUtc.AddSeconds(1.2),
                DurationMilliseconds = 200,
                Method = "POST",
                Url = "https://uploads.example.test/orders",
                StatusCode = 503
            }
        };

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using (var sessionStream = sessionEntry.Open())
            {
                await JsonSerializer.SerializeAsync(sessionStream, new SessionCaptureDocument
                {
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Session = snapshot
                }, protocolJson);
            }

            foreach (var request in requests)
            {
                var requestEntry = archive.CreateEntry($"network/requests/{request.Id}.json");
                await using var requestStream = requestEntry.Open();
                await JsonSerializer.SerializeAsync(requestStream, request, protocolJson);
            }
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }
}
