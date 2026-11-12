using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentAuditStoreTests
{
    [Fact]
    public void ListSummaries_CachesMetadataWithoutReloadingLargeTraceBodies()
    {
        using var environment = new StorageTestEnvironment();
        var store = new AuditStore(environment.ApplicationPaths);
        var audit = CreateAudit("large-trace", DateTimeOffset.UtcNow) with
        {
            AppId = "example.app",
            WorkspaceTestId = "checkout",
            WorkspaceTestName = "Checkout",
            BatchRunId = "batch-1",
            Reasoning = "balanced",
            CalculatedCost = new SimulatorAgentRunCost(1498, 1873, "USD", "calculated"),
            AgentPrompt = new string('x', 4 * 1024 * 1024)
        };
        var saved = store.Save(audit);
        Assert.Null(saved.ErrorMessage);

        var summary = Assert.Single(store.ListSummaries());
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var cached = store.ListSummaries();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Same(summary, Assert.Single(cached));
        Assert.True(allocatedBytes < 128 * 1024, $"Cached history allocated {allocatedBytes:N0} bytes.");
        Assert.Equal(audit.RunId, summary.RunId);
        Assert.Equal(audit.WorkspaceTestName, summary.WorkspaceTestName);
        Assert.Equal(audit.Tokens, summary.Tokens);
        Assert.Equal(audit.RequestedInstructions, summary.RequestedInstructions);
        Assert.Equal(1873, summary.CalculatedCost!.CostMicros);
        Assert.Equal("balanced", summary.Reasoning);

        var detailed = store.Load(summary);
        Assert.NotNull(detailed);
        Assert.Equal(audit.AgentPrompt, detailed.Audit.AgentPrompt);
        Assert.Equal(audit.CreateModelPassUsages(), detailed.Audit.CreateModelPassUsages());
    }

    [Fact]
    public void ListSummaries_RefreshesChangedAddedDeletedAndRepairedFiles()
    {
        using var environment = new StorageTestEnvironment();
        var writer = new AuditStore(environment.ApplicationPaths);
        var reader = new AuditStore(environment.ApplicationPaths);
        var audit = CreateAudit("original", DateTimeOffset.UtcNow);
        var saved = writer.Save(audit);
        Assert.Null(saved.ErrorMessage);
        var original = Assert.Single(reader.ListSummaries());

        // A second process can replace an audit without changing its byte length.
        var originalLength = new FileInfo(saved.FilePath!).Length;
        var json = File.ReadAllText(saved.FilePath!).Replace("Completed.", "Cancelled.", StringComparison.Ordinal);
        File.WriteAllText(saved.FilePath!, json);
        File.SetLastWriteTimeUtc(saved.FilePath!, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(originalLength, new FileInfo(saved.FilePath!).Length);
        var updated = Assert.Single(reader.ListSummaries());
        Assert.NotSame(original, updated);
        Assert.Equal("Cancelled.", updated.Message);

        var newer = writer.Save(CreateAudit("newer", audit.StartedUtc.AddMinutes(1)));
        Assert.Null(newer.ErrorMessage);
        Assert.Equal(["newer", "original"], reader.ListSummaries().Select(summary => summary.RunId));

        File.WriteAllText(newer.FilePath!, "{broken");
        Assert.Equal("original", Assert.Single(reader.ListSummaries()).RunId);
        Assert.Null(writer.Save(CreateAudit("newer", audit.StartedUtc.AddMinutes(1))).ErrorMessage);
        Assert.Equal(2, reader.ListSummaries().Count);

        File.Delete(saved.FilePath!);
        Assert.Equal("newer", Assert.Single(reader.ListSummaries()).RunId);
    }

    [Fact]
    public void ListSummaries_SaveInvalidatesAnAlreadyCachedRun()
    {
        using var environment = new StorageTestEnvironment();
        var store = new AuditStore(environment.ApplicationPaths);
        var audit = CreateAudit("updated-run", DateTimeOffset.UtcNow);
        Assert.Null(store.Save(audit).ErrorMessage);
        Assert.Single(store.ListSummaries());

        Assert.Null(store.Save(audit with { Message = "Updated." }).ErrorMessage);

        Assert.Equal("Updated.", Assert.Single(store.ListSummaries()).Message);
    }
}
