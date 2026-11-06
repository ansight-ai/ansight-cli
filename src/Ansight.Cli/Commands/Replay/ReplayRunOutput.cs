using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Replay;

internal sealed record ReplayRunOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    bool PlanOnly,
    string BetaFeatureNotice,
    ReplayPlan Plan,
    string? TargetSessionId,
    string? TargetAppId,
    SimulatorAgentRunResult? Result,
    ReplaySessionTaggingOutput? SessionTagging);
