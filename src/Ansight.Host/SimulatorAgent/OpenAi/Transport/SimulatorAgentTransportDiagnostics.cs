using System.Globalization;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentTransportDiagnostics(
    IReadOnlyList<SimulatorAgentTransportAttempt> Attempts,
    int CompactionCount)
{
    public SimulatorAgentTokenUsage WarmupTokens { get; init; } = SimulatorAgentTokenUsage.Empty;

    public IReadOnlyList<SimulatorAgentModelPassUsage> WarmupResponses { get; init; } = [];

    public long RequestBytes => Attempts.Sum(static attempt => (long)attempt.RequestBytes);
}
