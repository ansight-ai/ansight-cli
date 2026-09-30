using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Session;

public sealed class SessionTelemetryAnalysisCommandsTests
{
    private const long Megabyte = 1024L * 1024L;
    private static readonly JsonSerializerOptions protocolJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TelemetryAlias_StillReturnsRawMetricSamples()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportTelemetrySessionAsync(runtime, directory.Path);

        var result = await RunAsync(
            runtime,
            directory.Path,
            ["session", "telemetry", session.SessionId, "--json", "--data-dir", directory.Path]);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("ansight.session-metrics/v1", payload["schema"]!.GetValue<string>());
        Assert.Equal(4, payload["samples"]!.AsArray().Count);
    }

    [Fact]
    public async Task AnalyzeTelemetry_ReturnsFpsDropsAndMemorySpikes()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportTelemetrySessionAsync(runtime, directory.Path);

        var result = await RunAsync(
            runtime,
            directory.Path,
            ["session", "telemetry", "analyze", session.SessionId, "--json", "--data-dir", directory.Path]);
        var payload = JsonNode.Parse(result.StandardOutput)!.AsObject();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal("ansight.session-telemetry-analysis/v1", payload["schema"]!.GetValue<string>());
        Assert.Equal("all", payload["requestedKind"]!.GetValue<string>());
        Assert.Equal(2, payload["detectionCount"]!.GetValue<int>());
        Assert.Equal(15, payload["memorySpikeThresholds"]!["minimumIncreasePercent"]!.GetValue<int>());
        Assert.Equal(128, payload["memorySpikeThresholds"]!["minimumIncreaseMegabytes"]!.GetValue<int>());

        var fpsDrop = Assert.Single(payload["fpsDrops"]!.AsArray())!.AsObject();
        Assert.Equal(60, fpsDrop["baselineFps"]!.GetValue<long>());
        Assert.Equal(15, fpsDrop["minimumFps"]!.GetValue<long>());
        Assert.Equal("Critical", fpsDrop["severity"]!.GetValue<string>());

        var memorySpike = Assert.Single(payload["memorySpikes"]!.AsArray())!.AsObject();
        Assert.Equal(150 * Megabyte, memorySpike["deltaBytes"]!.GetValue<long>());
        Assert.Equal(150d, memorySpike["deltaPercent"]!.GetValue<double>());
        Assert.Equal("Major", memorySpike["severity"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnalyzeTelemetry_AppliesKindThresholdAndDetectionExitCode()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportTelemetrySessionAsync(runtime, directory.Path);

        var memoryResult = await RunAsync(
            runtime,
            directory.Path,
            [
                "session", "telemetry", "analyze", session.SessionId,
                "--kind", "memory-spike",
                "--memory-min-mb", "256",
                "--json",
                "--data-dir", directory.Path
            ]);
        var memoryPayload = JsonNode.Parse(memoryResult.StandardOutput)!.AsObject();
        Assert.Equal(CliExitCodes.Success, memoryResult.ExitCode);
        Assert.Empty(memoryPayload["fpsDrops"]!.AsArray());
        Assert.Empty(memoryPayload["memorySpikes"]!.AsArray());

        var gateResult = await RunAsync(
            runtime,
            directory.Path,
            [
                "session", "telemetry", "analyze", session.SessionId,
                "--kind", "fps-drop",
                "--fail-on-detection",
                "--json",
                "--data-dir", directory.Path
            ]);
        var gatePayload = JsonNode.Parse(gateResult.StandardOutput)!.AsObject();
        Assert.Equal(CliExitCodes.TelemetryAnalysisDetected, gateResult.ExitCode);
        Assert.Equal(1, gatePayload["detectionCount"]!.GetValue<int>());
        Assert.Empty(gatePayload["memorySpikes"]!.AsArray());
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

    private static async Task<AppSessionSnapshot> ImportTelemetrySessionAsync(
        RuntimeCoordinator runtime,
        string directoryPath)
    {
        var archivePath = Path.Combine(directoryPath, "telemetry-session.zip");
        var createdUtc = DateTimeOffset.Parse("2026-08-19T01:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = $"cli-telemetry-{Guid.NewGuid():N}",
            AppId = "com.example.cli-telemetry",
            ClientName = "CLI Telemetry",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = true,
            MetricChannels =
            [
                Channel(1, "Physical Footprint", "memory", "bytes"),
                Channel(3, "FPS", "frames", "fps")
            ],
            Metrics =
            [
                Sample(1, 100 * Megabyte, createdUtc),
                Sample(3, 60, createdUtc),
                Sample(3, 15, createdUtc.AddSeconds(1)),
                Sample(1, 250 * Megabyte, createdUtc.AddSeconds(2))
            ],
            TotalMetricChannelCount = 2,
            TotalMetricSampleCount = 4
        };
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using var sessionStream = sessionEntry.Open();
            await JsonSerializer.SerializeAsync(sessionStream, new SessionCaptureDocument
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Session = snapshot
            }, protocolJson);
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }

    private static SessionMetricSample Sample(byte channelId, long value, DateTimeOffset capturedAtUtc)
    {
        return new SessionMetricSample
        {
            ChannelId = channelId,
            Value = value,
            CapturedAtUtc = capturedAtUtc
        };
    }

    private static SessionMetricChannel Channel(byte channelId, string name, string type, string unit)
    {
        return new SessionMetricChannel
        {
            ChannelId = channelId,
            Name = name,
            ColorHex = "#000000",
            Type = type,
            Unit = unit
        };
    }
}
