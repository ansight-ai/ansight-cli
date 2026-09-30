using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal static class SessionLogInspection
{
    public static RequestResult BuildGetLogsResult(SessionResolver sessionResolver, JsonObject? arguments)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return ToolError(resolutionError);
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", OperationDefaults.DefaultLogResultLimit, OperationDefaults.MaxLogResultLimit, out var limit, out errorMessage)
            || !ArgumentReader.TryReadMinimumVerbosity(arguments, out var minimumVerbosity, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid log filters.");
        }

        var tags = ArgumentReader.ReadStringSet(arguments, "tags");
        var streamIds = ArgumentReader.ReadStringSet(arguments, "streamIds");
        var matchedLogs = snapshot!.Logs
            .Where(log => PayloadJson.MatchesTimestamp(log.TimestampUtc, startUtc, endUtc))
            .Where(log => !minimumVerbosity.HasValue || PayloadJson.MatchesMinimumVerbosity(log.Priority, minimumVerbosity.Value))
            .Where(log => tags.Count == 0 || (!string.IsNullOrWhiteSpace(log.Tag) && tags.Contains(log.Tag.Trim())))
            .Where(log => streamIds.Count == 0 || streamIds.Contains(log.StreamId))
            .OrderBy(log => log.TimestampUtc)
            .ToArray();

        var returnedLogs = matchedLogs.Length > limit
            ? matchedLogs.TakeLast(limit).ToArray()
            : matchedLogs;

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["clientName"] = snapshot.ClientName,
                ["status"] = snapshot.Status,
                ["filters"] = new JsonObject
                {
                    ["startUtc"] = startUtc,
                    ["endUtc"] = endUtc,
                    ["minimumVerbosity"] = minimumVerbosity?.ToString(),
                    ["streamIds"] = PayloadJson.CreateJsonArray(streamIds.OrderBy(streamId => streamId, StringComparer.OrdinalIgnoreCase).Select(streamId => JsonValue.Create(streamId))),
                    ["tags"] = PayloadJson.CreateJsonArray(tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).Select(tag => JsonValue.Create(tag)))
                },
                ["matchedLogCount"] = matchedLogs.Length,
                ["returnedLogCount"] = returnedLogs.Length,
                ["isTruncated"] = matchedLogs.Length > returnedLogs.Length,
                ["logs"] = PayloadJson.CreateJsonArray(returnedLogs.Select(log => (JsonNode?)BuildLogPayload(log)))
            },
            isError: false);
    }

    public static RequestResult BuildSearchLogsResult(
        IRuntimeState runtimeState,
        SessionResolver sessionResolver,
        JsonObject? arguments)
    {
        var query = NormalizeOptionalString(arguments?["query"]?.GetValue<string>());
        if (query is null)
        {
            return ToolError("query is required.");
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", OperationDefaults.DefaultLogResultLimit, OperationDefaults.MaxLogResultLimit, out var limit, out errorMessage)
            || !ArgumentReader.TryReadMinimumVerbosity(arguments, out var minimumVerbosity, out errorMessage))
        {
            return ToolError(errorMessage ?? "Invalid log search filters.");
        }

        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        var includeHistorical = arguments?["includeHistorical"]?.GetValue<bool>() ?? true;
        var liveOnly = arguments?["liveOnly"]?.GetValue<bool>() ?? false;
        var tags = ArgumentReader.ReadStringSet(arguments, "tags");
        var sources = ArgumentReader.ReadStringSet(arguments, "sources");
        var streamIds = ArgumentReader.ReadStringSet(arguments, "streamIds");
        var platformFilters = SessionPlatformFilters.Read(arguments);
        var liveSessionIds = sessionResolver.GetLiveSessionIds();
        var candidateSessions = runtimeState.GetSessionSummaries()
            .Where(snapshot => sessionId is null || string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            .Where(snapshot => appId is null || string.Equals(snapshot.AppId, appId, StringComparison.Ordinal))
            .Where(snapshot => includeHistorical || liveSessionIds.Contains(snapshot.SessionId))
            .Where(snapshot => !liveOnly || liveSessionIds.Contains(snapshot.SessionId))
            .OrderByDescending(snapshot => liveSessionIds.Contains(snapshot.SessionId))
            .ThenByDescending(snapshot => snapshot.LastUpdatedUtc)
            .ToArray();

        var searchedSessionCount = 0;
        var matchedLogs = new List<SessionLogSearchMatch>();
        foreach (var resolvedSnapshot in sessionResolver.LoadResolvedSnapshots(candidateSessions))
        {
            searchedSessionCount++;
            var platformKey = SessionPlatformFilters.ResolveSessionPlatformKey(resolvedSnapshot);
            if (platformFilters.Count > 0 && !platformFilters.Contains(platformKey))
            {
                continue;
            }

            var osName = SessionPlatformFilters.ResolveSessionOperatingSystem(resolvedSnapshot);
            var isLive = liveSessionIds.Contains(resolvedSnapshot.SessionId);
            foreach (var log in resolvedSnapshot.Logs)
            {
                if (!PayloadJson.MatchesTimestamp(log.TimestampUtc, startUtc, endUtc)
                    || (minimumVerbosity.HasValue && !PayloadJson.MatchesMinimumVerbosity(log.Priority, minimumVerbosity.Value))
                    || (tags.Count > 0 && (string.IsNullOrWhiteSpace(log.Tag) || !tags.Contains(log.Tag.Trim())))
                    || (sources.Count > 0 && (string.IsNullOrWhiteSpace(log.Source) || !sources.Contains(log.Source.Trim())))
                    || (streamIds.Count > 0 && !streamIds.Contains(log.StreamId))
                    || !MatchesLogQuery(log, query))
                {
                    continue;
                }

                matchedLogs.Add(new SessionLogSearchMatch(resolvedSnapshot, log, isLive, platformKey, osName));
            }
        }

        var returnedLogs = matchedLogs
            .OrderByDescending(match => match.Log.TimestampUtc)
            .ThenBy(match => match.Session.SessionId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        var matchedSessionCount = matchedLogs
            .Select(match => match.Session.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["query"] = query,
                ["filters"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["appId"] = appId,
                    ["platforms"] = PayloadJson.CreateJsonArray(platformFilters.OrderBy(platform => platform, StringComparer.OrdinalIgnoreCase).Select(platform => JsonValue.Create(platform))),
                    ["includeHistorical"] = includeHistorical,
                    ["liveOnly"] = liveOnly,
                    ["startUtc"] = startUtc,
                    ["endUtc"] = endUtc,
                    ["minimumVerbosity"] = minimumVerbosity?.ToString(),
                    ["streamIds"] = PayloadJson.CreateJsonArray(streamIds.OrderBy(streamId => streamId, StringComparer.OrdinalIgnoreCase).Select(streamId => JsonValue.Create(streamId))),
                    ["tags"] = PayloadJson.CreateJsonArray(tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).Select(tag => JsonValue.Create(tag))),
                    ["sources"] = PayloadJson.CreateJsonArray(sources.OrderBy(source => source, StringComparer.OrdinalIgnoreCase).Select(source => JsonValue.Create(source)))
                },
                ["searchedSessionCount"] = searchedSessionCount,
                ["matchedSessionCount"] = matchedSessionCount,
                ["matchedLogCount"] = matchedLogs.Count,
                ["returnedLogCount"] = returnedLogs.Length,
                ["isTruncated"] = matchedLogs.Count > returnedLogs.Length,
                ["logs"] = PayloadJson.CreateJsonArray(returnedLogs.Select(match => (JsonNode?)BuildLogSearchMatchPayload(match)))
            },
            isError: false);
    }

    public static bool MatchesLogQuery(LogEntry log, string query)
    {
        return log.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(log.Tag) && log.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(log.Source) && log.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(log.EventId) && log.EventId.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject BuildLogPayload(LogEntry log)
    {
        return new JsonObject
        {
            ["streamId"] = log.StreamId,
            ["timestampUtc"] = log.TimestampUtc,
            ["priority"] = log.Priority.ToString(),
            ["source"] = log.Source,
            ["tag"] = log.Tag,
            ["processId"] = log.ProcessId,
            ["threadId"] = log.ThreadId,
            ["message"] = log.Message
        };
    }

    private static JsonObject BuildLogSearchMatchPayload(SessionLogSearchMatch match)
    {
        return new JsonObject
        {
            ["sessionId"] = match.Session.SessionId,
            ["appId"] = match.Session.AppId,
            ["clientName"] = match.Session.ClientName,
            ["appName"] = match.Session.DeviceProfile?.App?.AppName,
            ["platformKey"] = match.PlatformKey,
            ["osName"] = match.OperatingSystemName,
            ["isLive"] = match.IsLive,
            ["streamId"] = match.Log.StreamId,
            ["timestampUtc"] = match.Log.TimestampUtc,
            ["priority"] = match.Log.Priority.ToString(),
            ["source"] = match.Log.Source,
            ["tag"] = match.Log.Tag,
            ["eventId"] = match.Log.EventId,
            ["processId"] = match.Log.ProcessId,
            ["threadId"] = match.Log.ThreadId,
            ["message"] = match.Log.Message
        };
    }

    private static RequestResult ToolError(string message)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = message
            },
            isError: true);
    }

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
