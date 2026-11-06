using System.IO.Compression;
using System.Text.Json;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionAgentHistoryTests
{
    [Fact]
    public async Task EmptyHistory_IsSuccessfulWithoutClaimingAgentSubmissionSupport()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var source = new CliRemoteAgentChatSource(runtime);

        var result = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Tasks);
        Assert.False(source.SupportsSubmission);
    }

    [Fact]
    public async Task History_ReturnsPersistedLinksInOrderAndRespectsSessionAndLimit()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var first = await ImportSessionAsync(runtime, directory.Path, "session-first", 0);
        await ImportSessionAsync(runtime, directory.Path, "session-second", 1);

        var source = new CliRemoteAgentChatSource(runtime);

        var all = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest());
        Assert.True(all.IsSuccess);
        Assert.Equal(2, all.Tasks.Count);
        Assert.Equal("task-session-second", all.Tasks[0].ProviderTaskId);

        var limited = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest(Limit: 1));
        Assert.Equal("task-session-second", Assert.Single(limited.Tasks).ProviderTaskId);

        var filtered = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest(first.SessionId));
        Assert.Equal("task-session-first", Assert.Single(filtered.Tasks).ProviderTaskId);

        var missing = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest("missing"));
        Assert.Empty(missing.Tasks);
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

    private static async Task<AppSessionSnapshot> ImportSessionAsync(
        RuntimeCoordinator runtime,
        string directoryPath,
        string sessionId,
        int hour)
    {
        var timestamp = new DateTimeOffset(2026, 11, 6, hour, 0, 0, TimeSpan.Zero);
        var archivePath = Path.Combine(directoryPath, $"{sessionId}.zip");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = sessionId,
            AppId = "com.example.companion",
            ClientName = "Companion test",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = timestamp,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = timestamp,
            IsHistorical = true,
            MetricChannels = [],
            Metrics = [],
            AgentTaskLinks =
            [
                new SessionAgentTaskLink
                {
                    LinkId = $"link-{sessionId}",
                    SessionId = sessionId,
                    AnnotationBatchId = "batch-1",
                    Source = "simulator.remote.mobile",
                    Provider = "codex",
                    ProviderTaskId = $"task-{sessionId}",
                    SubmittedPrompt = "Review the saved annotation.",
                    Status = "completed",
                    CreatedAtUtc = timestamp,
                    UpdatedAtUtc = timestamp
                }            ]
        };

        using (var stream = File.Create(archivePath))
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("session.json");
                await using var sessionStream = entry.Open();
                await JsonSerializer.SerializeAsync(sessionStream, new SessionCaptureDocument
                {
                    SavedAtUtc = timestamp,
                    Session = snapshot
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }
}
