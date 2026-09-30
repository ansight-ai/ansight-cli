using System.Diagnostics;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class WorkspaceLaunchTiming(IProgress<WorkspaceTestRunProgress>? progress) : IProgress<WorkspaceTestRunProgress>
{
    private readonly List<SimulatorAgentStartupStep> steps = [];
    private readonly Stopwatch timer = new();
    private string? stage;
    private DateTimeOffset startedUtc;
    private bool completed;

    public void Report(WorkspaceTestRunProgress value)
    {
        if (!completed)
        {
            if (value.Stage is "app.launched" or "session.selected")
            {
                FinishStage();
                completed = value.Stage == "session.selected";
            }
            else
            {
                var name = value.Stage switch
                {
                    "app.inspect" => "Inspect application package",
                    "target.resolve" => "Resolve device and installed app",
                    "device.start" => "Start device and wait for boot readiness",
                    "device.show-window" => "Show device window",
                    "app.check-install" => "Check installed application checksum",
                    "app.install" => "Install application",
                    "app.reuse" => "Reuse installed application",
                    "app.stop" => "Stop application before launch",
                    "enrollment.issue" => "Issue device enrollment",
                    "app.launch" => "Launch application",
                    "session.wait" => "Wait for app to connect to Ansight",
                    _ => null
                };
                if (name is not null)
                {
                    FinishStage();
                    stage = name;
                    startedUtc = DateTimeOffset.UtcNow;
                    timer.Restart();
                }
            }
        }
        progress?.Report(value);
    }

    public IReadOnlyList<SimulatorAgentStartupStep> Complete(string status = "succeeded")
    {
        FinishStage(status);
        completed = true;
        return steps.ToArray();
    }

    private void FinishStage(string status = "succeeded")
    {
        if (stage is null) return;
        steps.Add(new(stage, startedUtc, timer.ElapsedMilliseconds, status));
        stage = null;
        timer.Reset();
    }
}
