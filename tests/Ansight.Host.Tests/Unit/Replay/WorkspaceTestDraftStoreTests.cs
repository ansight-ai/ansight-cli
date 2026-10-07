using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Replay;

public sealed class WorkspaceTestDraftStoreTests
{
    [Fact]
    public async Task GeneratedYamlAndEditsSurviveStoreRecreation()
    {
        using var environment = new TestEnvironment();
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var extraction = new WorkspaceTestExtraction("copy-eagle-rock-location", "id: copy-eagle-rock-location", 3, []);
        var request = new WorkspaceTestDraftSaveRequest(
            null, "recorded-session", start, start.AddSeconds(30), "Copy Eagle Rock Location",
            extraction, extraction.Source, ["approach", "share"], false, "Location copied", "Use search", "fast", false, null, null);
        var store = new WorkspaceTestDraftStore(environment.ApplicationPaths.ApplicationDataPath);

        var generated = await store.SaveAsync("com.example.app", request, CancellationToken.None);
        var reopened = new WorkspaceTestDraftStore(environment.ApplicationPaths.ApplicationDataPath);
        var listed = await reopened.ListAsync("recorded-session", CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(generated.DraftId, listed[0].DraftId);
        Assert.Equal(extraction.Source, listed[0].Source);

        var edited = await reopened.SaveAsync("com.example.app", request with
        {
            DraftId = generated.DraftId,
            Source = "id: copy-eagle-rock-location\nname: Edited",
            LastTraceRunId = "trace-123"
        }, CancellationToken.None);
        Assert.Equal(generated.CreatedAtUtc, edited.CreatedAtUtc);
        Assert.Equal("trace-123", (await store.ListAsync("recorded-session", CancellationToken.None))[0].LastTraceRunId);
        Assert.Empty(await store.ListAsync("different-session", CancellationToken.None));
        Assert.True(await reopened.DiscardAsync("recorded-session", generated.DraftId, CancellationToken.None));
        Assert.Empty(await store.ListAsync("recorded-session", CancellationToken.None));
    }
}
