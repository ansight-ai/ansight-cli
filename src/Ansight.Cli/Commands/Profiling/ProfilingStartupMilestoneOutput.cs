using Ansight.Host;

namespace Ansight.Cli.Commands.Profiling;

internal sealed record ProfilingStartupMilestoneOutput(
    string Id,
    double TimestampMilliseconds,
    string Description);
