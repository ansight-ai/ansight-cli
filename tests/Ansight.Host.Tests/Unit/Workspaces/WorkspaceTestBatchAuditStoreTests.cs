using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceTestBatchAuditStoreTests
{
    [Fact]
    public void Save_UpdatesOneProtectedBatchRecordAndListsIt()
    {
        using var environment = new TestEnvironment();
        var store = new WorkspaceTestBatchAuditStore(environment.ApplicationPaths);
        var startedUtc = DateTimeOffset.Parse("2026-08-22T01:02:03Z");
        var audit = new WorkspaceTestBatchRunAudit(
            1,
            "batch-123",
            "cli",
            environment.RootPath,
            ["onboarding.smoke"],
            "gpt-5.6-terra",
            64,
            64,
            512,
            true,
            true,
            null,
            null,
            startedUtc,
            null,
            WorkspaceTestHistoryStatuses.Running,
            "Running one test.",
            [new WorkspaceTestBatchItemAudit(
                1,
                "onboarding.smoke",
                "Onboarding smoke",
                "com.example.app",
                WorkspaceTestHistoryStatuses.Pending,
                "Waiting to run.",
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0)]);

        var startedSave = store.Save(audit);
        var completedSave = store.Save(audit with
        {
            CompletedUtc = startedUtc.AddSeconds(12),
            Status = WorkspaceTestHistoryStatuses.Succeeded,
            Message = "Complete.",
            Tests = [audit.Tests[0] with
            {
                Status = WorkspaceTestHistoryStatuses.Succeeded,
                Message = "Passed.",
                AgentRunId = "agent-123",
                TotalTokens = 1200
            }]
        });

        Assert.Null(startedSave.ErrorMessage);
        Assert.Null(completedSave.ErrorMessage);
        Assert.Equal(startedSave.FilePath, completedSave.FilePath);
        var history = Assert.Single(store.List());
        Assert.Equal("batch-123", history.Audit.BatchRunId);
        Assert.Equal(WorkspaceTestHistoryStatuses.Succeeded, history.Audit.Status);
        Assert.Equal(1, history.Audit.PassedCount);
        Assert.Equal(1200, history.Audit.TotalTokens);
        Assert.Equal("agent-123", Assert.Single(history.Audit.Tests).AgentRunId);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(history.FilePath));
        }
    }
}
