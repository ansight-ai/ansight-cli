using Ansight.Host.Workspaces.Execution;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceLaunchTimingTests
{
    [Fact]
    public void CapturesPerformedLaunchStagesAndStopsBeforeExecution()
    {
        var timing = new WorkspaceLaunchTiming(null);
        foreach (var stage in new[] { "target.resolve", "device.start", "app.check-install", "app.reuse", "app.stop", "app.launch", "app.launched", "session.wait", "session.selected", "app.stop" })
            timing.Report(new WorkspaceTestRunProgress(stage, "Progress"));
        var steps = timing.Complete();
        Assert.Equal(7, steps.Count);
        Assert.DoesNotContain(steps, step => step.Name == "Install application");
        Assert.Equal("Wait for app to connect to Ansight", steps[^1].Name);
        Assert.All(steps, step => Assert.True(step.DurationMilliseconds >= 0));
    }
}
