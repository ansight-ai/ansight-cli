namespace Ansight.Cli.Commands.App;

internal sealed record AppGraphAgentRouteDiscoveryResult(
    IReadOnlyList<SimulatorAgentAppGraphPlan> Plans,
    IReadOnlyList<string> Warnings)
{
    public static AppGraphAgentRouteDiscoveryResult Empty { get; } = new([], []);

    public static AppGraphAgentRouteDiscoveryResult Unavailable(string message)
        => new([], string.IsNullOrWhiteSpace(message) ? [] : [message.Trim()]);
}
