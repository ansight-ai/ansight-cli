using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionUiTestRunTests
{
    [Fact]
    public void TestMetadataSurvivesSdkPropertyReplacementAndSessionPersistence()
    {
        using var environment = new TestEnvironment();
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var runtime = composition.Get<RuntimeCoordinator>();
        var state = composition.Get<IRuntimeState>();
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("android", "phone", "Phone", "test.app", false, false, false)
        { ExecutionMode = "device", DeviceKind = DeviceKinds.Physical });
        state.UpdateSessionMetadata(id, true, ["keep"], "Keep notes", "My recording");
        state.SetSessionCustomProperties(id, new JsonObject { ["app"] = new JsonObject { ["tenant"] = "first" } });
        var run = new SessionUiTestRun("checkout", "Checkout", "run-1", "running", "task", DateTimeOffset.UtcNow);
        state.SetSessionUiTestRun(id, run);
        state.TryGetSessionSnapshot(id, out var running);
        Assert.Equal("first", running!.CustomProperties!["app"]!["tenant"]!.GetValue<string>());

        state.SetSessionCustomProperties(id, new JsonObject
        {
            ["app"] = new JsonObject { ["tenant"] = "second" },
            ["ui-test"] = new JsonObject { ["status"] = "stale" }
        });
        state.SetSessionUiTestRun(id, run with { Status = "succeeded", CompletedUtc = DateTimeOffset.UtcNow });
        state.TryGetSessionSnapshot(id, out var completed);
        Assert.Equal("second", completed!.CustomProperties!["app"]!["tenant"]!.GetValue<string>());
        Assert.Equal("succeeded", completed.CustomProperties["ui-test"]!["status"]!.GetValue<string>());
        Assert.Equal("My recording", completed.Name);
        Assert.True(completed.IsPinned);
        Assert.Equal(["keep"], completed.Tags);
        Assert.Equal("Keep notes", completed.Notes);

        var store = new SessionCaptureStore(new DataToolApplicationPaths(Path.Combine(environment.RootPath, "roundtrip")));
        store.Save(completed);
        Assert.True(store.TryLoad(id, out var restored));
        Assert.True(JsonNode.DeepEquals(completed.CustomProperties, restored!.CustomProperties));
        Assert.True(JsonNode.DeepEquals(completed.CustomProperties, Assert.Single(store.LoadSummaries()).CustomProperties));

        state.SetSessionCustomProperties(id, null);
        state.TryGetSessionSnapshot(id, out var cleared);
        Assert.Equal("run-1", cleared!.CustomProperties!["ui-test"]!["runId"]!.GetValue<string>());
        Assert.Null(cleared.CustomProperties["app"]);
    }
}
