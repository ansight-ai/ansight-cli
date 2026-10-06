using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class RepositoryRunHistoryTests
{
    [Fact]
    public void AutomationHistory_CompactsOversizedExistingRunsAndBoundsNewOutput()
    {
        using var directory = TestDirectory.Create();
        var run = CreateAutomationRun();
        var automationPath = Path.Combine(directory.Path, "automation");
        Directory.CreateDirectory(automationPath);
        var filePath = Path.Combine(automationPath, "trigger-runs.jsonl");
        var oversized = run with
        {
            Output = new JsonObject { ["payload"] = new string('x', 1_000_000) }
        };
        File.WriteAllLines(filePath, Enumerable.Repeat(
            JsonSerializer.Serialize(oversized, JsonUtil.Compact), 20));

        var store = new RepositoryAutomationRunStore(directory.Path);
        store.Append(oversized);

        Assert.True(new FileInfo(filePath).Length <= BoundedRunHistory.MaximumFileBytes);
        var recent = store.GetRecent(run.AppId, 1);
        Assert.True(Assert.Single(recent).Output?["truncated"]?.GetValue<bool>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(filePath));
        }
    }

    private static AutomationRunCompletedEvent CreateAutomationRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new AutomationRunCompletedEvent(
            "run-1", 1, 1, false, null, "/tmp/repository", "com.example.app",
            "session-1", "trigger-1", "automation-1", "task", "task-1",
            "event-1", "correlation-1", AutomationRunStatus.Succeeded,
            now, now, 0, "completed", null, string.Empty,
            new AutomationEventEnvelope
            {
                EventId = "event-1",
                Kind = "session.log_received",
                OccurredAtUtc = now,
                AppId = "com.example.app",
                SessionId = "session-1",
                CorrelationId = "correlation-1"
            });
    }
}
