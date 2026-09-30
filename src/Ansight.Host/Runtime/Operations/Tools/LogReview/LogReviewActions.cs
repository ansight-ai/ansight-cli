using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed partial class LogReviewActions
{
    private readonly IRuntimeState runtimeState;
    private readonly SessionResolver sessionResolver;

    public LogReviewActions(OperationServices services)
    {
        runtimeState = services.RuntimeState;
        sessionResolver = services.SessionResolver;
    }

    public RequestResult BuildGetLogContextResult(JsonObject? arguments)
    {
        if (!TryResolveSingleLogReviewSession(arguments, out var session, out var errorMessage))
        {
            return CreateToolError(errorMessage ?? "Unable to resolve session.");
        }

        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log context timestamp.");
        }

        if (!TryReadOptionalIntegerArgument(arguments, "logIndex", out var logIndex, out errorMessage)
            || !TryReadBoundedNonNegativeInteger(arguments, "before", OperationDefaults.DefaultLogContextRadius, OperationDefaults.MaxLogContextRadius, out var before, out errorMessage)
            || !TryReadBoundedNonNegativeInteger(arguments, "after", OperationDefaults.DefaultLogContextRadius, OperationDefaults.MaxLogContextRadius, out var after, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log context arguments.");
        }

        var eventId = NormalizeOptionalString(arguments?["eventId"]?.GetValue<string>());
        if (!logIndex.HasValue && eventId is null && !timestampUtc.HasValue)
        {
            return CreateToolError("One of logIndex, eventId, or timestampUtc is required.");
        }

        var logs = BuildIndexedLogs(session!.Snapshot).ToArray();
        if (logs.Length == 0)
        {
            return CreateToolError($"Session '{session.Snapshot.SessionId}' has no captured logs.");
        }

        var target = ResolveLogContextTarget(logs, logIndex, eventId, timestampUtc, out var targetSelector, out errorMessage);
        if (target is null)
        {
            return CreateToolError(errorMessage ?? "Target log was not found.");
        }

        var startIndex = Math.Max(0, target.Index - before);
        var endIndex = Math.Min(logs.Length - 1, target.Index + after);
        var contextLogs = logs
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .ToArray();

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = BuildLogReviewSessionPayload(session),
                ["targetSelector"] = targetSelector,
                ["targetLogIndex"] = target.Index,
                ["before"] = before,
                ["after"] = after,
                ["totalLogCount"] = logs.Length,
                ["returnedLogCount"] = contextLogs.Length,
                ["hasEarlierLogs"] = startIndex > 0,
                ["hasLaterLogs"] = endIndex < logs.Length - 1,
                ["logs"] = PayloadJson.CreateJsonArray(contextLogs.Select(entry => (JsonNode?)BuildSessionLogEntryPayload(session, entry, entry.Index == target.Index)))
            },
            isError: false);
    }

    public RequestResult BuildSummarizeLogWindowResult(JsonObject? arguments)
    {
        if (!TryResolveLogReviewSessions(arguments, out var sessions, out var sessionFilters, out var errorMessage))
        {
            return CreateToolError(errorMessage ?? "Unable to resolve sessions.");
        }

        if (!TryReadLogReviewFilters(arguments, out var filters, out errorMessage)
            || !TryReadPositiveInteger(arguments, "topCount", OperationDefaults.DefaultLogReviewTopCount, OperationDefaults.MaxLogReviewTopCount, out var topCount, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log summary filters.");
        }

        var entries = GetFilteredLogEntries(sessions, filters).ToArray();
        var matchedSessionIds = entries
            .Select(entry => entry.Session.Snapshot.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["filters"] = BuildLogReviewFiltersPayload(sessionFilters, filters),
                ["searchedSessionCount"] = sessions.Count,
                ["matchedSessionCount"] = matchedSessionIds.Count,
                ["matchedLogCount"] = entries.Length,
                ["firstLogUtc"] = entries.Length == 0 ? null : entries[0].Log.TimestampUtc,
                ["lastLogUtc"] = entries.Length == 0 ? null : entries[^1].Log.TimestampUtc,
                ["priorityCounts"] = BuildPriorityCountsArray(entries.Select(entry => entry.Log)),
                ["tagCounts"] = BuildStringCountsArray(entries.Select(entry => entry.Log.Tag), topCount),
                ["sourceCounts"] = BuildStringCountsArray(entries.Select(entry => entry.Log.Source), topCount),
                ["repeatedMessages"] = BuildRepeatedMessagesArray(entries, topCount),
                ["notableLogs"] = PayloadJson.CreateJsonArray(entries
                    .Where(entry => entry.Log.Priority >= LogPriority.Warning && entry.Log.Priority != LogPriority.Unknown)
                    .OrderByDescending(entry => entry.Log.Priority)
                    .ThenBy(entry => entry.Log.TimestampUtc)
                    .Take(topCount)
                    .Select(entry => (JsonNode?)BuildSessionLogEntryPayload(entry, isTarget: false))),
                ["sessions"] = PayloadJson.CreateJsonArray(sessions
                    .Where(session => matchedSessionIds.Contains(session.Snapshot.SessionId))
                    .OrderBy(session => session.Snapshot.AppId, StringComparer.Ordinal)
                    .ThenBy(session => session.Snapshot.SessionId, StringComparer.Ordinal)
                    .Select(session => (JsonNode?)BuildLogReviewSessionPayload(
                        session,
                        entries.Count(entry => string.Equals(entry.Session.Snapshot.SessionId, session.Snapshot.SessionId, StringComparison.Ordinal)))))
            },
            isError: false);
    }

    public RequestResult BuildGetLogFacetsResult(JsonObject? arguments)
    {
        if (!TryResolveLogReviewSessions(arguments, out var sessions, out var sessionFilters, out var errorMessage))
        {
            return CreateToolError(errorMessage ?? "Unable to resolve sessions.");
        }

        if (!TryReadLogReviewFilters(arguments, out var filters, out errorMessage)
            || !TryReadPositiveInteger(arguments, "topCount", OperationDefaults.DefaultLogReviewTopCount, OperationDefaults.MaxLogReviewTopCount, out var topCount, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log facet filters.");
        }

        var entries = GetFilteredLogEntries(sessions, filters).ToArray();
        var matchedSessionIds = entries
            .Select(entry => entry.Session.Snapshot.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["filters"] = BuildLogReviewFiltersPayload(sessionFilters, filters),
                ["searchedSessionCount"] = sessions.Count,
                ["matchedSessionCount"] = matchedSessionIds.Count,
                ["matchedLogCount"] = entries.Length,
                ["apps"] = BuildAppFacetArray(entries, topCount),
                ["platforms"] = BuildPlatformFacetArray(entries),
                ["priorities"] = BuildPriorityCountsArray(entries.Select(entry => entry.Log)),
                ["tags"] = BuildStringCountsArray(entries.Select(entry => entry.Log.Tag), topCount),
                ["sources"] = BuildStringCountsArray(entries.Select(entry => entry.Log.Source), topCount),
                ["sessions"] = BuildSessionFacetArray(entries, topCount)
            },
            isError: false);
    }

    public RequestResult BuildGetLogTimelineResult(JsonObject? arguments)
    {
        if (!TryResolveLogReviewSessions(arguments, out var sessions, out var sessionFilters, out var errorMessage))
        {
            return CreateToolError(errorMessage ?? "Unable to resolve sessions.");
        }

        if (!TryReadLogReviewFilters(arguments, out var filters, out errorMessage)
            || !TryReadPositiveInteger(arguments, "bucketCount", OperationDefaults.DefaultLogTimelineBucketCount, OperationDefaults.MaxLogTimelineBucketCount, out var bucketCount, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log timeline filters.");
        }

        var entries = GetFilteredLogEntries(sessions, filters).ToArray();
        var range = ResolveTimelineRange(sessions, filters, entries);
        var buckets = BuildTimelineBuckets(entries, range.StartUtc, range.EndUtc, bucketCount);

        return RequestResult.ToolResult(
            new JsonObject
            {
                ["filters"] = BuildLogReviewFiltersPayload(sessionFilters, filters),
                ["searchedSessionCount"] = sessions.Count,
                ["matchedSessionCount"] = entries
                    .Select(entry => entry.Session.Snapshot.SessionId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                ["matchedLogCount"] = entries.Length,
                ["startUtc"] = range.StartUtc,
                ["endUtc"] = range.EndUtc,
                ["bucketCount"] = bucketCount,
                ["buckets"] = buckets
            },
            isError: false);
    }

    public RequestResult BuildExportLogSliceResult(JsonObject? arguments)
    {
        if (!TryResolveLogReviewSessions(arguments, out var sessions, out var sessionFilters, out var errorMessage))
        {
            return CreateToolError(errorMessage ?? "Unable to resolve sessions.");
        }

        if (!TryReadLogReviewFilters(arguments, out var filters, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", OperationDefaults.DefaultLogResultLimit, OperationDefaults.MaxLogResultLimit, out var limit, out errorMessage)
            || !TryReadExportFormat(arguments, out var format, out errorMessage)
            || !TryReadBooleanArgument(arguments, "includeAnnotations", defaultValue: true, out var includeAnnotations, out errorMessage)
            || !TryReadBooleanArgument(arguments, "includeScreenshots", defaultValue: true, out var includeScreenshots, out errorMessage)
            || !TryReadBooleanArgument(arguments, "includeTelemetrySummary", defaultValue: true, out var includeTelemetrySummary, out errorMessage))
        {
            return CreateToolError(errorMessage ?? "Invalid log export arguments.");
        }

        var entries = GetFilteredLogEntries(sessions, filters).ToArray();
        var returnedEntries = entries.Take(limit).ToArray();
        var exportRange = ResolveExportRange(sessions, filters, returnedEntries);
        var annotations = includeAnnotations
            ? BuildExportAnnotationsArray(sessions, exportRange.StartUtc, exportRange.EndUtc)
            : new JsonArray();
        var screenshots = includeScreenshots
            ? BuildExportScreenshotsArray(sessions, exportRange.StartUtc, exportRange.EndUtc)
            : new JsonArray();
        var telemetrySummary = includeTelemetrySummary
            ? BuildExportTelemetrySummaryArray(sessions, exportRange.StartUtc, exportRange.EndUtc)
            : new JsonArray();

        var payload = new JsonObject
        {
            ["format"] = format,
            ["filters"] = BuildLogReviewFiltersPayload(sessionFilters, filters),
            ["searchedSessionCount"] = sessions.Count,
            ["matchedLogCount"] = entries.Length,
            ["returnedLogCount"] = returnedEntries.Length,
            ["isTruncated"] = entries.Length > returnedEntries.Length,
            ["startUtc"] = exportRange.StartUtc,
            ["endUtc"] = exportRange.EndUtc,
            ["sessions"] = PayloadJson.CreateJsonArray(sessions
                .OrderBy(session => session.Snapshot.AppId, StringComparer.Ordinal)
                .ThenBy(session => session.Snapshot.SessionId, StringComparer.Ordinal)
                .Select(session => (JsonNode?)BuildLogReviewSessionPayload(
                    session,
                    returnedEntries.Count(entry => string.Equals(entry.Session.Snapshot.SessionId, session.Snapshot.SessionId, StringComparison.Ordinal))))),
            ["logs"] = PayloadJson.CreateJsonArray(returnedEntries.Select(entry => (JsonNode?)BuildSessionLogEntryPayload(entry, isTarget: false))),
            ["annotations"] = annotations,
            ["screenshots"] = screenshots,
            ["telemetrySummary"] = telemetrySummary
        };

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "both", StringComparison.OrdinalIgnoreCase))
        {
            payload["markdown"] = BuildMarkdownLogSlice(
                sessions,
                returnedEntries,
                annotations,
                screenshots,
                telemetrySummary,
                exportRange.StartUtc,
                exportRange.EndUtc,
                entries.Length);
        }

        return RequestResult.ToolResult(payload, isError: false);
    }

    private bool TryResolveSingleLogReviewSession(JsonObject? arguments, out LogReviewSession? session, out string? errorMessage)
    {
        session = null;
        errorMessage = null;
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            errorMessage = resolutionError;
            return false;
        }

        var liveSessionIds = sessionResolver.GetLiveSessionIds();
        var liveOnly = arguments?["liveOnly"]?.GetValue<bool>() ?? false;
        if (liveOnly && !liveSessionIds.Contains(snapshot!.SessionId))
        {
            errorMessage = $"Session '{snapshot.SessionId}' is not currently connected.";
            return false;
        }

        var platformFilters = SessionPlatformFilters.Read(arguments);
        var platformKey = SessionPlatformFilters.ResolveSessionPlatformKey(snapshot!);
        if (platformFilters.Count > 0 && !platformFilters.Contains(platformKey))
        {
            errorMessage = $"Session '{snapshot!.SessionId}' platform '{platformKey}' does not match the requested platform filters.";
            return false;
        }

        session = new LogReviewSession(
            snapshot!,
            liveSessionIds.Contains(snapshot!.SessionId),
            platformKey,
            SessionPlatformFilters.ResolveSessionOperatingSystem(snapshot!));
        return true;
    }

    public bool TryResolveLogReviewSessions(
        JsonObject? arguments,
        out IReadOnlyList<LogReviewSession> sessions,
        out LogReviewSessionFilters sessionFilters,
        out string? errorMessage)
    {
        errorMessage = null;
        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        var includeHistorical = arguments?["includeHistorical"]?.GetValue<bool>() ?? true;
        var liveOnly = arguments?["liveOnly"]?.GetValue<bool>() ?? false;
        var platformFilters = SessionPlatformFilters.Read(arguments);
        var liveSessionIds = sessionResolver.GetLiveSessionIds();

        sessionFilters = new LogReviewSessionFilters(
            sessionId,
            appId,
            platformFilters.ToArray(),
            includeHistorical,
            liveOnly);

        var candidateSessions = runtimeState.GetSessionSummaries()
            .Where(snapshot => sessionId is null || string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            .Where(snapshot => appId is null || string.Equals(snapshot.AppId, appId, StringComparison.Ordinal))
            .Where(snapshot => includeHistorical || liveSessionIds.Contains(snapshot.SessionId))
            .Where(snapshot => !liveOnly || liveSessionIds.Contains(snapshot.SessionId))
            .OrderByDescending(snapshot => liveSessionIds.Contains(snapshot.SessionId))
            .ThenByDescending(snapshot => snapshot.LastUpdatedUtc)
            .ToArray();

        var resolvedSessions = new List<LogReviewSession>();
        foreach (var resolvedSnapshot in sessionResolver.LoadResolvedSnapshots(candidateSessions))
        {
            var platformKey = SessionPlatformFilters.ResolveSessionPlatformKey(resolvedSnapshot);
            if (platformFilters.Count > 0 && !platformFilters.Contains(platformKey))
            {
                continue;
            }

            resolvedSessions.Add(new LogReviewSession(
                resolvedSnapshot,
                liveSessionIds.Contains(resolvedSnapshot.SessionId),
                platformKey,
                SessionPlatformFilters.ResolveSessionOperatingSystem(resolvedSnapshot)));
        }

        sessions = resolvedSessions;
        return true;
    }

    public static bool TryReadLogReviewFilters(JsonObject? arguments, out LogReviewFilters filters, out string? errorMessage)
    {
        filters = new LogReviewFilters(null, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);
        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadMinimumVerbosity(arguments, out var minimumVerbosity, out errorMessage))
        {
            return false;
        }

        if (startUtc.HasValue && endUtc.HasValue && startUtc.Value > endUtc.Value)
        {
            errorMessage = "startUtc must be before or equal to endUtc.";
            return false;
        }

        filters = new LogReviewFilters(
            startUtc,
            endUtc,
            minimumVerbosity,
            ArgumentReader.ReadStringSet(arguments, "streamIds").ToArray(),
            ArgumentReader.ReadStringSet(arguments, "tags").ToArray(),
            ArgumentReader.ReadStringSet(arguments, "sources").ToArray(),
            NormalizeOptionalString(arguments?["query"]?.GetValue<string>()));
        return true;
    }

}
