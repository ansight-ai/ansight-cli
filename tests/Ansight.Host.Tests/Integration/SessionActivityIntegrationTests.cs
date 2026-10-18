using System.IO.Compression;
using System.Net.Http.Json;
using Ansight.Host.Activity;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class SessionActivityIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task PlayerEditsExposePaginatedActivityAndDeletionRetainsTheJournal()
    {
        using var environment = new TestEnvironment();
        var snapshot = Snapshot();
        new SessionCaptureStore(environment.ApplicationPaths).Save(snapshot);
        await using var runtime = environment.CreateRuntime();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient { BaseAddress = explorer.ExplorerUrl };
        using var response = await client.PostAsJsonAsync($"api/sessions/{snapshot.SessionId}/metadata",
            new { name = "Edited", isPinned = false, notes = "Private annotation text" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = await client.GetFromJsonAsync<SessionActivityPage>($"api/sessions/{snapshot.SessionId}/activity?limit=1");
        var start = Assert.Single(first!.Records);
        Assert.Equal("player", start.Caller.Ingress);
        Assert.Equal("unknown", start.Caller.Kind);
        var second = await client.GetFromJsonAsync<SessionActivityPage>($"api/sessions/{snapshot.SessionId}/activity?limit=1&cursor={first.NextCursor}");
        Assert.Equal("succeeded", Assert.Single(second!.Records).Outcome);
        var raw = await client.GetStringAsync($"api/sessions/{snapshot.SessionId}/activity?format=jsonl");
        Assert.Equal(2, raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("Private annotation text", raw);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"api/sessions/{snapshot.SessionId}/activity?limit=501")).StatusCode);
        Assert.True(runtime.SessionEditing.Delete(snapshot.SessionId).IsSuccess);
        var after = runtime.Activity.Read(snapshot.SessionId);
        Assert.Equal("session.Delete", after.Records[^1].Operation);
        Assert.Equal("succeeded", after.Records[^1].Outcome);
        Assert.False(runtime.Sessions.TryGetSnapshot(snapshot.SessionId, out _));
        Assert.NotEmpty(await client.GetStringAsync($"api/sessions/{snapshot.SessionId}/activity"));
    }

    [Fact(Timeout = 60000)]
    public async Task ArchiveRoundTripLabelsImportedIdentityAndSanitizedExportsOmitIt()
    {
        using var environment = new TestEnvironment();
        new SessionCaptureStore(environment.ApplicationPaths).Save(Snapshot());
        await using var runtime = environment.CreateRuntime();
        using var caller = SessionActivityContext.Push(new(new ActivityCaller
        { Kind = "agent", Name = "assistant", PrincipalId = "account-123", PrincipalAttribution = "authenticated", ExecutorAttribution = "host-generated" }));
        Assert.True(runtime.SessionEditing.UpdateMetadata("activity-session", false, [], null, "Edited").IsSuccess);
        var archive = Path.Combine(environment.RootPath, "activity.zip");
        Assert.True((await runtime.SessionArchives.ExportSessionArchiveAsync("activity-session", archive)).IsSuccess);
        using var importedEnvironment = new TestEnvironment();
        await using var importedRuntime = importedEnvironment.CreateRuntime();
        var result = await importedRuntime.SessionArchives.ImportSessionArchiveAsync(archive);
        Assert.True(result.IsSuccess, result.Message);
        var allRecords = importedRuntime.Activity.Read(result.ImportedSession!.SessionId).Records;
        Assert.Contains(allRecords, record => record.Operation == "session.import" && record.Provenance == "local" && record.Outcome == "succeeded");
        var records = allRecords.Where(record => record.Operation != "session.import").ToArray();
        Assert.NotEmpty(records);
        Assert.All(records, record =>
        {
            Assert.Equal("imported", record.Provenance);
            Assert.Equal("imported", record.Caller.PrincipalAttribution);
            Assert.Equal("imported", record.Caller.ExecutorAttribution);
        });
        Assert.DoesNotContain(records, record => runtime.Activity.Read("activity-session").Records.Any(local => local.OperationId == record.OperationId));
        var sanitized = Path.Combine(environment.RootPath, "sanitized.zip");
        var export = await runtime.SessionArchives.ExportSessionArchiveAsync("activity-session", sanitized, new SessionArchiveExportOptions { Sanitize = true });
        Assert.True(export.IsSuccess, export.Message);
        using var zip = ZipFile.OpenRead(sanitized);
        Assert.Null(zip.GetEntry(SessionActivityArchive.EntryPath));
    }

    private static AppSessionSnapshot Snapshot() => new()
    {
        SessionId = "activity-session", AppId = "test.activity", ClientName = "Activity test", RemoteAddress = "127.0.0.1",
        CreatedUtc = DateTimeOffset.Parse("2026-10-17T00:00:00Z"), LastUpdatedUtc = DateTimeOffset.Parse("2026-10-17T00:01:00Z"),
        Status = "Complete", IsHistorical = true, ConfigId = null, MetricChannels = [], Metrics = []
    };
}
