using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed record AppGraphLiveUpdateResult(
    bool IsSuccess,
    string Message,
    int DestinationsAccepted,
    int TransitionsAccepted,
    int ActionsAccepted,
    int NavigationHostsAccepted,
    int TabGroupsAccepted,
    int TotalDestinations,
    int TotalTransitions,
    IReadOnlyList<SimulatorAgentAppGraphLiveAction> PendingActions)
{
    public IReadOnlyDictionary<string, string> ReconciledActionIds { get; init; }
        = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> ReconciledTransitionIds { get; init; }
        = new Dictionary<string, string>();

    public static AppGraphLiveUpdateResult Failure(string message)
        => new(false, message, 0, 0, 0, 0, 0, 0, 0, []);

    public static AppGraphLiveUpdateResult Success(
        int destinationsAccepted,
        int transitionsAccepted,
        int actionsAccepted,
        int navigationHostsAccepted,
        int tabGroupsAccepted,
        int totalDestinations,
        int totalTransitions,
        IReadOnlyList<SimulatorAgentAppGraphLiveAction> pendingActions)
        => new(
            true,
            $"Live App Graph now contains {totalDestinations} destinations, {totalTransitions} transitions, "
            + $"and {pendingActions.Count} pending action(s).",
            destinationsAccepted,
            transitionsAccepted,
            actionsAccepted,
            navigationHostsAccepted,
            tabGroupsAccepted,
            totalDestinations,
            totalTransitions,
            pendingActions);
}
