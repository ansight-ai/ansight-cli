using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal static class AppGraphExploreTargetResolver
{
    public static string ResolveAppIdForLaunch(
        string requestedTarget,
        IReadOnlyList<AppSessionSnapshot> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedTarget);
        ArgumentNullException.ThrowIfNull(sessions);

        var normalizedTarget = requestedTarget.Trim();

        return sessions
            .Where(session => session.SessionId.Equals(normalizedTarget, StringComparison.Ordinal))
            .OrderByDescending(static session => session.LastUpdatedUtc)
            .Select(static session => session.AppId)
            .FirstOrDefault()
            ?? normalizedTarget;
    }

    public static AppGraphExploreLiveTarget Resolve(
        string requestedAppId,
        string? requestedSessionId,
        IReadOnlyList<AppSessionSnapshot> sessions,
        IReadOnlyList<string> connectedSessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedAppId);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(connectedSessionIds);

        var normalizedTarget = requestedAppId.Trim();
        var connected = connectedSessionIds.ToHashSet(StringComparer.Ordinal);
        var liveSessions = sessions
            .Where(session => connected.Contains(session.SessionId))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            var normalizedSessionId = requestedSessionId.Trim();
            var selected = liveSessions.FirstOrDefault(session =>
                session.SessionId.Equals(normalizedSessionId, StringComparison.Ordinal));

            if (selected is null)
            {
                throw new CliHostUnavailableException(
                    $"Session '{normalizedSessionId}' is not currently linked to the Ansight host.");
            }

            if (!selected.AppId.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliUsageException(
                    $"Session '{normalizedSessionId}' belongs to app '{selected.AppId}', not targeted app '{normalizedTarget}'.");
            }

            return new AppGraphExploreLiveTarget(selected.AppId, selected, UsedLegacySessionTarget: false);
        }

        var legacySession = liveSessions.FirstOrDefault(session =>
            session.SessionId.Equals(normalizedTarget, StringComparison.Ordinal));

        if (legacySession is not null)
        {
            return new AppGraphExploreLiveTarget(
                legacySession.AppId,
                legacySession,
                UsedLegacySessionTarget: true);
        }

        var selectedForApp = liveSessions
            .Where(session => session.AppId.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static session => session.LastUpdatedUtc)
            .FirstOrDefault();

        if (selectedForApp is null)
        {
            throw new CliHostUnavailableException(
                $"No live session is linked to app '{normalizedTarget}'. Run interactively to launch it, pass --launch, or open the app yourself and retry after the Ansight connection notification.");
        }

        return new AppGraphExploreLiveTarget(
            selectedForApp.AppId,
            selectedForApp,
            UsedLegacySessionTarget: false);
    }
}
