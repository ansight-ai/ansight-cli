using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed partial class WorkspaceTestServiceTests
{
    [Fact]
    public void History_ListsMetadataAndLoadsOnlyTheRequestedTrace()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var store = new AuditStore(runtime.ApplicationPaths);
        var startedUtc = DateTimeOffset.UtcNow;
        var selected = CreateHistoryAudit("selected", "selected-batch", startedUtc) with
        {
            AgentPrompt = "Selected run details."
        };
        var unrelated = CreateHistoryAudit("unrelated", "other-batch", startedUtc.AddMinutes(1)) with
        {
            AgentPrompt = new string('x', 4 * 1024 * 1024),
            AppId = "other.app"
        };
        Assert.Null(store.Save(selected).ErrorMessage);
        Assert.Null(store.Save(unrelated).ErrorMessage);
        var history = runtime.WorkspaceTests.History;
        history.List();
        history.Inspect("selected");

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var listed = history.List(appId: selected.AppId, limit: 1);
        var inspected = history.Inspect("selected", selected.AppId);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(allocatedBytes < 1024 * 1024, $"History loaded unrelated trace data: {allocatedBytes:N0} bytes allocated.");
        Assert.Equal("selected", Assert.Single(listed.Runs).RunId);
        Assert.NotNull(inspected);
        Assert.Equal(selected.AgentPrompt, Assert.Single(inspected.Runs).Audit.AgentPrompt);
        Assert.Null(history.Inspect("selected", "other.app"));
    }
}
