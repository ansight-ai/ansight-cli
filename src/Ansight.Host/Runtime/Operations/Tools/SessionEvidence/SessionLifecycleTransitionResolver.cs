using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal readonly record struct ResolvedSessionLifecycleTransition(
    DateTimeOffset TimestampUtc,
    AppLifecycleState State);

internal static class SessionLifecycleTransitionResolver
{
    public static IReadOnlyList<ResolvedSessionLifecycleTransition> Build(AppSessionSnapshot snapshot)
    {
        var transitions = new List<ResolvedSessionLifecycleTransition>();

        foreach (var appEvent in snapshot.ApplicationEvents)
        {
            if (TryParseState(appEvent.Label, out var state))
            {
                transitions.Add(new ResolvedSessionLifecycleTransition(
                    appEvent.CapturedAtUtc.ToUniversalTime(),
                    state));
            }
        }

        foreach (var log in snapshot.Logs)
        {
            if (string.Equals(log.Tag, "LIFECYCLE", StringComparison.OrdinalIgnoreCase)
                && TryParseState(log.Message, out var state))
            {
                transitions.Add(new ResolvedSessionLifecycleTransition(
                    log.TimestampUtc.ToUniversalTime(),
                    state));
            }
        }

        if (snapshot.AppStateChangedUtc.HasValue
            && snapshot.AppState is AppLifecycleState.Foreground or AppLifecycleState.Background)
        {
            transitions.Add(new ResolvedSessionLifecycleTransition(
                snapshot.AppStateChangedUtc.Value.ToUniversalTime(),
                snapshot.AppState));
        }

        return Normalize(transitions);
    }

    private static IReadOnlyList<ResolvedSessionLifecycleTransition> Normalize(
        IEnumerable<ResolvedSessionLifecycleTransition> transitions)
    {
        var result = new List<ResolvedSessionLifecycleTransition>();
        foreach (var transition in transitions.OrderBy(static item => item.TimestampUtc))
        {
            if (result.Count > 0 && result[^1].State == transition.State)
            {
                continue;
            }

            result.Add(transition);
        }

        return result;
    }

    private static bool TryParseState(string? value, out AppLifecycleState state)
    {
        if (value?.Contains("lifecycle.foreground", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("App moved to foreground", StringComparison.OrdinalIgnoreCase) == true)
        {
            state = AppLifecycleState.Foreground;
            return true;
        }

        if (value?.Contains("lifecycle.background", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("App moved to background", StringComparison.OrdinalIgnoreCase) == true)
        {
            state = AppLifecycleState.Background;
            return true;
        }

        state = AppLifecycleState.Unknown;
        return false;
    }
}
