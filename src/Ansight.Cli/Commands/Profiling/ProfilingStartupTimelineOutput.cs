using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingStartupTimelineOutput(
    double DurationMilliseconds,
    IReadOnlyList<ProfilingStartupMilestoneOutput> Milestones,
    bool ApplicationReadyMarkerObserved,
    IReadOnlyList<string> Limitations);
