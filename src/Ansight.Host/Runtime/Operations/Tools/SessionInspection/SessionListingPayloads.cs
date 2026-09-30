using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionListingPayloads
{
    public static JsonObject BuildListDevicesPayload(
        IRuntimeState runtimeState,
        SessionResolver sessionResolver,
        JsonObject? arguments)
    {
        var includeHistorical = arguments?["includeHistorical"]?.GetValue<bool>() ?? false;
        var appId = arguments?["appId"]?.GetValue<string>();
        var filters = SessionListingFilters.Read(arguments);
        var liveSessionIds = sessionResolver.GetLiveSessionIds();

        var sessions = runtimeState.GetSessionSummaries()
            .Where(snapshot => includeHistorical || liveSessionIds.Contains(snapshot.SessionId))
            .Where(snapshot => string.IsNullOrWhiteSpace(appId) || string.Equals(snapshot.AppId, appId, StringComparison.Ordinal))
            .Where(snapshot => SessionListingFilters.Matches(snapshot, filters))
            .OrderByDescending(snapshot => liveSessionIds.Contains(snapshot.SessionId))
            .ThenByDescending(snapshot => snapshot.LastUpdatedUtc)
            .Select(snapshot => PayloadJson.BuildDevicePayload(snapshot, liveSessionIds.Contains(snapshot.SessionId)))
            .ToArray();

        return new JsonObject
        {
            ["count"] = sessions.Length,
            ["includeHistorical"] = includeHistorical,
            ["filters"] = SessionListingFilters.BuildPayload(filters),
            ["devices"] = PayloadJson.CreateJsonArray(sessions)
        };
    }

    public static JsonObject BuildListSessionsPayload(
        IRuntimeState runtimeState,
        SessionResolver sessionResolver,
        JsonObject? arguments)
    {
        const int defaultLimit = 100;
        const int maxLimit = 1000;

        var includeHistorical = arguments?["includeHistorical"]?.GetValue<bool>() ?? true;
        var liveOnly = arguments?["liveOnly"]?.GetValue<bool>() ?? false;
        var requireLogs = arguments?["hasLogs"]?.GetValue<bool>();
        var requireTelemetry = arguments?["hasTelemetry"]?.GetValue<bool>();
        var appId = arguments?["appId"]?.GetValue<string>();
        var limit = arguments?["limit"]?.GetValue<int?>() ?? defaultLimit;
        limit = Math.Clamp(limit, 1, maxLimit);
        var filters = SessionListingFilters.Read(arguments);

        var liveSessionIds = sessionResolver.GetLiveSessionIds();
        var candidateSessions = runtimeState.GetSessionSummaries()
            .Where(snapshot => includeHistorical || liveSessionIds.Contains(snapshot.SessionId))
            .Where(snapshot => !liveOnly || liveSessionIds.Contains(snapshot.SessionId))
            .Where(snapshot => string.IsNullOrWhiteSpace(appId) || string.Equals(snapshot.AppId, appId, StringComparison.Ordinal))
            .Where(snapshot => SessionListingFilters.Matches(snapshot, filters))
            .OrderByDescending(snapshot => liveSessionIds.Contains(snapshot.SessionId))
            .ThenByDescending(snapshot => snapshot.LastUpdatedUtc)
            .ToArray();

        AppSessionSnapshot[] matchedSessions;
        AppSessionSnapshot[] returnedSessions;
        if (requireLogs.HasValue || requireTelemetry.HasValue)
        {
            matchedSessions = sessionResolver.LoadResolvedSnapshots(candidateSessions)
                .Where(snapshot => !requireLogs.HasValue || (snapshot.Logs.Count > 0) == requireLogs.Value)
                .Where(snapshot => !requireTelemetry.HasValue || (snapshot.Metrics.Count > 0) == requireTelemetry.Value)
                .ToArray();
            returnedSessions = matchedSessions.Take(limit).ToArray();
        }
        else
        {
            matchedSessions = candidateSessions;
            returnedSessions = sessionResolver.LoadResolvedSnapshots(candidateSessions.Take(limit)).ToArray();
        }

        var sessions = returnedSessions
            .Select(snapshot => PayloadJson.BuildSessionPayload(snapshot, liveSessionIds.Contains(snapshot.SessionId)))
            .ToArray();

        return new JsonObject
        {
            ["count"] = sessions.Length,
            ["matchedCount"] = matchedSessions.Length,
            ["returnedCount"] = sessions.Length,
            ["isTruncated"] = matchedSessions.Length > sessions.Length,
            ["includeHistorical"] = includeHistorical,
            ["liveOnly"] = liveOnly,
            ["limit"] = limit,
            ["filters"] = SessionListingFilters.BuildPayload(filters),
            ["sessions"] = PayloadJson.CreateJsonArray(sessions)
        };
    }
}
