using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed partial class LogReviewActions
{
    public IEnumerable<SessionLogReviewEntry> GetFilteredLogEntries(
        IReadOnlyList<LogReviewSession> sessions,
        LogReviewFilters filters)
    {
        return sessions
            .SelectMany(session => BuildIndexedLogs(session.Snapshot)
                .Where(entry => MatchesLogReviewFilters(entry.Log, filters))
                .Select(entry => new SessionLogReviewEntry(session, entry.Index, entry.Log)))
            .OrderBy(entry => entry.Log.TimestampUtc)
            .ThenBy(entry => entry.Session.Snapshot.SessionId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Index);
    }

    private static bool MatchesLogReviewFilters(LogEntry log, LogReviewFilters filters)
    {
        return PayloadJson.MatchesTimestamp(log.TimestampUtc, filters.StartUtc, filters.EndUtc)
               && (!filters.MinimumVerbosity.HasValue || PayloadJson.MatchesMinimumVerbosity(log.Priority, filters.MinimumVerbosity.Value))
               && (filters.StreamIds.Count == 0 || filters.StreamIds.Contains(log.StreamId, StringComparer.OrdinalIgnoreCase))
               && (filters.Tags.Count == 0 || (!string.IsNullOrWhiteSpace(log.Tag) && filters.Tags.Contains(log.Tag.Trim(), StringComparer.OrdinalIgnoreCase)))
               && (filters.Sources.Count == 0 || (!string.IsNullOrWhiteSpace(log.Source) && filters.Sources.Contains(log.Source.Trim(), StringComparer.OrdinalIgnoreCase)))
               && (filters.Query is null || SessionLogInspection.MatchesLogQuery(log, filters.Query));
    }

    private static IEnumerable<IndexedLogEntry> BuildIndexedLogs(AppSessionSnapshot snapshot)
    {
        return snapshot.Logs
            .Select((log, originalIndex) => new IndexedLogEntry(originalIndex, log))
            .OrderBy(entry => entry.Log.TimestampUtc)
            .ThenBy(entry => entry.Index)
            .Select((entry, sortedIndex) => new IndexedLogEntry(sortedIndex, entry.Log));
    }

    private static IndexedLogEntry? ResolveLogContextTarget(
        IReadOnlyList<IndexedLogEntry> logs,
        int? logIndex,
        string? eventId,
        DateTimeOffset? timestampUtc,
        out string targetSelector,
        out string? errorMessage)
    {
        errorMessage = null;
        if (logIndex.HasValue)
        {
            targetSelector = "logIndex";
            if (logIndex.Value < 0 || logIndex.Value >= logs.Count)
            {
                errorMessage = $"logIndex must be between 0 and {logs.Count - 1}.";
                return null;
            }

            return logs[logIndex.Value];
        }

        if (eventId is not null)
        {
            targetSelector = "eventId";
            var eventMatch = logs.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.Log.EventId)
                && string.Equals(entry.Log.EventId, eventId, StringComparison.OrdinalIgnoreCase));
            if (eventMatch is null)
            {
                errorMessage = $"No log with eventId '{eventId}' was found.";
            }

            return eventMatch;
        }

        targetSelector = "timestampUtc";
        var targetUtc = timestampUtc!.Value;
        return logs.FirstOrDefault(entry => entry.Log.TimestampUtc >= targetUtc) ?? logs[^1];
    }

    public static JsonObject BuildLogReviewFiltersPayload(LogReviewSessionFilters sessionFilters, LogReviewFilters filters)
    {
        return new JsonObject
        {
            ["sessionId"] = sessionFilters.SessionId,
            ["appId"] = sessionFilters.AppId,
            ["platforms"] = PayloadJson.CreateJsonArray(sessionFilters.Platforms
                .OrderBy(platform => platform, StringComparer.OrdinalIgnoreCase)
                .Select(platform => JsonValue.Create(platform))),
            ["includeHistorical"] = sessionFilters.IncludeHistorical,
            ["liveOnly"] = sessionFilters.LiveOnly,
            ["startUtc"] = filters.StartUtc,
            ["endUtc"] = filters.EndUtc,
            ["minimumVerbosity"] = filters.MinimumVerbosity?.ToString(),
            ["tags"] = PayloadJson.CreateJsonArray(filters.Tags
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .Select(tag => JsonValue.Create(tag))),
            ["sources"] = PayloadJson.CreateJsonArray(filters.Sources
                .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                .Select(source => JsonValue.Create(source))),
            ["query"] = filters.Query
        };
    }

    private static JsonObject BuildLogReviewSessionPayload(LogReviewSession session, int? matchedLogCount = null)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = session.Snapshot.SessionId,
            ["appId"] = session.Snapshot.AppId,
            ["clientName"] = session.Snapshot.ClientName,
            ["appName"] = session.Snapshot.DeviceProfile?.App?.AppName,
            ["status"] = session.Snapshot.Status,
            ["platformKey"] = session.PlatformKey,
            ["osName"] = session.OperatingSystemName,
            ["isLive"] = session.IsLive,
            ["isHistorical"] = session.Snapshot.IsHistorical,
            ["createdUtc"] = session.Snapshot.CreatedUtc,
            ["lastUpdatedUtc"] = session.Snapshot.LastUpdatedUtc,
            ["logCount"] = session.Snapshot.Logs.Count
        };

        if (matchedLogCount.HasValue)
        {
            payload["matchedLogCount"] = matchedLogCount.Value;
        }

        return payload;
    }

    public static JsonObject BuildSessionLogEntryPayload(SessionLogReviewEntry entry, bool isTarget)
        => BuildSessionLogEntryPayload(entry.Session, new IndexedLogEntry(entry.Index, entry.Log), isTarget);

    private static JsonObject BuildSessionLogEntryPayload(LogReviewSession session, IndexedLogEntry entry, bool isTarget)
    {
        return new JsonObject
        {
            ["sessionId"] = session.Snapshot.SessionId,
            ["appId"] = session.Snapshot.AppId,
            ["clientName"] = session.Snapshot.ClientName,
            ["appName"] = session.Snapshot.DeviceProfile?.App?.AppName,
            ["platformKey"] = session.PlatformKey,
            ["osName"] = session.OperatingSystemName,
            ["isLive"] = session.IsLive,
            ["logIndex"] = entry.Index,
            ["streamId"] = entry.Log.StreamId,
            ["isTarget"] = isTarget,
            ["timestampUtc"] = entry.Log.TimestampUtc,
            ["priority"] = entry.Log.Priority.ToString(),
            ["source"] = entry.Log.Source,
            ["tag"] = entry.Log.Tag,
            ["eventId"] = entry.Log.EventId,
            ["processId"] = entry.Log.ProcessId,
            ["threadId"] = entry.Log.ThreadId,
            ["message"] = entry.Log.Message
        };
    }

    private static JsonArray BuildPriorityCountsArray(IEnumerable<LogEntry> logs)
    {
        return PayloadJson.CreateJsonArray(logs
            .GroupBy(log => log.Priority)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => (JsonNode?)new JsonObject
            {
                ["priority"] = group.Key.ToString(),
                ["count"] = group.Count()
            }));
    }

    private static JsonArray BuildStringCountsArray(IEnumerable<string?> values, int limit)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawValue in values)
        {
            var value = NormalizeOptionalString(rawValue);
            if (value is null)
            {
                continue;
            }

            counts[value] = counts.TryGetValue(value, out var existingCount)
                ? existingCount + 1
                : 1;
        }

        return PayloadJson.CreateJsonArray(counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(pair => (JsonNode?)new JsonObject
            {
                ["value"] = pair.Key,
                ["count"] = pair.Value
            }));
    }

    private static JsonArray BuildRepeatedMessagesArray(IReadOnlyList<SessionLogReviewEntry> entries, int limit)
    {
        var repeatedMessages = entries
            .GroupBy(entry => NormalizeLogMessage(entry.Log.Message), StringComparer.Ordinal)
            .Select(group => new LogReviewMessageGroup(
                group.Key,
                group.First().Log.Message,
                group.Count(),
                group.Min(entry => entry.Log.TimestampUtc),
                group.Max(entry => entry.Log.TimestampUtc)))
            .Where(group => group.Count > 1)
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.FirstUtc)
            .Take(limit);

        return PayloadJson.CreateJsonArray(repeatedMessages.Select(group => (JsonNode?)new JsonObject
        {
            ["message"] = group.SampleMessage,
            ["normalizedMessage"] = group.NormalizedMessage,
            ["count"] = group.Count,
            ["firstUtc"] = group.FirstUtc,
            ["lastUtc"] = group.LastUtc
        }));
    }

    private static JsonArray BuildAppFacetArray(IReadOnlyList<SessionLogReviewEntry> entries, int limit)
    {
        return PayloadJson.CreateJsonArray(entries
            .GroupBy(entry => entry.Session.Snapshot.AppId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(group =>
            {
                var firstEntry = group.First();
                return (JsonNode?)new JsonObject
                {
                    ["appId"] = group.Key,
                    ["appName"] = firstEntry.Session.Snapshot.DeviceProfile?.App?.AppName,
                    ["sessionCount"] = group
                        .Select(entry => entry.Session.Snapshot.SessionId)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    ["logCount"] = group.Count()
                };
            }));
    }

    private static JsonArray BuildPlatformFacetArray(IReadOnlyList<SessionLogReviewEntry> entries)
    {
        return PayloadJson.CreateJsonArray(entries
            .GroupBy(entry => entry.Session.PlatformKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (JsonNode?)new JsonObject
            {
                ["platformKey"] = group.Key,
                ["sessionCount"] = group
                    .Select(entry => entry.Session.Snapshot.SessionId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                ["logCount"] = group.Count(),
                ["osNames"] = BuildStringCountsArray(group.Select(entry => entry.Session.OperatingSystemName), OperationDefaults.DefaultLogReviewTopCount)
            }));
    }

    private static JsonArray BuildSessionFacetArray(IReadOnlyList<SessionLogReviewEntry> entries, int limit)
    {
        return PayloadJson.CreateJsonArray(entries
            .GroupBy(entry => entry.Session.Snapshot.SessionId, StringComparer.Ordinal)
            .Select(group => new LogReviewSessionFacet(group.First().Session, group.Count(), group.Min(entry => entry.Log.TimestampUtc), group.Max(entry => entry.Log.TimestampUtc)))
            .OrderByDescending(facet => facet.LogCount)
            .ThenByDescending(facet => facet.LastLogUtc)
            .Take(limit)
            .Select(facet =>
            {
                var payload = BuildLogReviewSessionPayload(facet.Session, facet.LogCount);
                payload["firstLogUtc"] = facet.FirstLogUtc;
                payload["lastLogUtc"] = facet.LastLogUtc;
                return (JsonNode?)payload;
            }));
    }

    private static LogReviewRange ResolveTimelineRange(
        IReadOnlyList<LogReviewSession> sessions,
        LogReviewFilters filters,
        IReadOnlyList<SessionLogReviewEntry> entries)
    {
        var startUtc = filters.StartUtc
                       ?? (entries.Count > 0
                           ? entries[0].Log.TimestampUtc
                           : sessions.Count > 0
                               ? sessions.Min(session => session.Snapshot.CreatedUtc)
                               : DateTimeOffset.UtcNow);
        var endUtc = filters.EndUtc
                     ?? (entries.Count > 0
                         ? entries[^1].Log.TimestampUtc
                         : sessions.Count > 0
                             ? sessions.Max(session => session.Snapshot.LastUpdatedUtc)
                             : startUtc);

        if (endUtc <= startUtc)
        {
            endUtc = startUtc.AddSeconds(1);
        }

        return new LogReviewRange(startUtc.ToUniversalTime(), endUtc.ToUniversalTime());
    }

    private static JsonArray BuildTimelineBuckets(
        IReadOnlyList<SessionLogReviewEntry> entries,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bucketCount)
    {
        var totalTicks = Math.Max(1L, endUtc.UtcTicks - startUtc.UtcTicks);
        var bucketTicks = Math.Max(1L, (long)Math.Ceiling(totalTicks / (double)bucketCount));
        var buckets = new List<List<SessionLogReviewEntry>>(bucketCount);
        for (var index = 0; index < bucketCount; index++)
        {
            buckets.Add(new List<SessionLogReviewEntry>());
        }

        foreach (var entry in entries)
        {
            var offsetTicks = Math.Clamp(entry.Log.TimestampUtc.UtcTicks - startUtc.UtcTicks, 0L, totalTicks);
            var bucketIndex = (int)Math.Min(bucketCount - 1, offsetTicks / bucketTicks);
            buckets[bucketIndex].Add(entry);
        }

        var payloadBuckets = new JsonArray();
        for (var index = 0; index < bucketCount; index++)
        {
            var bucketStartOffsetTicks = Math.Min(totalTicks, bucketTicks * index);
            var bucketEndOffsetTicks = index == bucketCount - 1
                ? totalTicks
                : Math.Min(totalTicks, bucketTicks * (index + 1));
            if (bucketEndOffsetTicks < bucketStartOffsetTicks)
            {
                bucketEndOffsetTicks = bucketStartOffsetTicks;
            }

            var bucketStartUtc = startUtc.AddTicks(bucketStartOffsetTicks);
            var bucketEndUtc = index == bucketCount - 1
                ? endUtc
                : startUtc.AddTicks(bucketEndOffsetTicks);
            var bucketEntries = buckets[index];
            payloadBuckets.Add(new JsonObject
            {
                ["index"] = index,
                ["startUtc"] = bucketStartUtc,
                ["endUtc"] = bucketEndUtc,
                ["logCount"] = bucketEntries.Count,
                ["sessionCount"] = bucketEntries
                    .Select(entry => entry.Session.Snapshot.SessionId)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                ["priorityCounts"] = BuildPriorityCountsArray(bucketEntries.Select(entry => entry.Log)),
                ["topTags"] = BuildStringCountsArray(bucketEntries.Select(entry => entry.Log.Tag), OperationDefaults.DefaultLogReviewTopCount)
            });
        }

        return payloadBuckets;
    }

    private static LogReviewRange ResolveExportRange(
        IReadOnlyList<LogReviewSession> sessions,
        LogReviewFilters filters,
        IReadOnlyList<SessionLogReviewEntry> returnedEntries)
    {
        var startUtc = filters.StartUtc
                       ?? (returnedEntries.Count > 0
                           ? returnedEntries[0].Log.TimestampUtc
                           : sessions.Count > 0
                               ? sessions.Min(session => session.Snapshot.CreatedUtc)
                               : DateTimeOffset.UtcNow);
        var endUtc = filters.EndUtc
                     ?? (returnedEntries.Count > 0
                         ? returnedEntries[^1].Log.TimestampUtc
                         : sessions.Count > 0
                             ? sessions.Max(session => session.Snapshot.LastUpdatedUtc)
                             : startUtc);

        if (endUtc < startUtc)
        {
            endUtc = startUtc;
        }

        return new LogReviewRange(startUtc.ToUniversalTime(), endUtc.ToUniversalTime());
    }

    private static JsonArray BuildExportAnnotationsArray(
        IReadOnlyList<LogReviewSession> sessions,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        return PayloadJson.CreateJsonArray(sessions
            .SelectMany(session => session.Snapshot.Annotations
                .Where(annotation => SessionAnnotationInspection.MatchesAnnotationTimeRange(annotation, startUtc, endUtc))
                .OrderBy(annotation => annotation.StartUtc)
                .Select(annotation =>
                {
                    var payload = PayloadJson.BuildSessionAnnotationPayload(annotation);
                    payload["sessionId"] = session.Snapshot.SessionId;
                    payload["appId"] = session.Snapshot.AppId;
                    payload["platformKey"] = session.PlatformKey;
                    return (JsonNode?)payload;
                })));
    }

    private static JsonArray BuildExportScreenshotsArray(
        IReadOnlyList<LogReviewSession> sessions,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        return PayloadJson.CreateJsonArray(sessions
            .SelectMany(session => session.Snapshot.Images
                .Where(image => PayloadJson.MatchesTimestamp(image.CapturedAtUtc, startUtc, endUtc))
                .OrderBy(image => image.CapturedAtUtc)
                .Select(image => (JsonNode?)new JsonObject
                {
                    ["sessionId"] = session.Snapshot.SessionId,
                    ["appId"] = session.Snapshot.AppId,
                    ["platformKey"] = session.PlatformKey,
                    ["frameId"] = image.FrameId,
                    ["capturedAtUtc"] = image.CapturedAtUtc,
                    ["format"] = image.Format,
                    ["width"] = image.Width,
                    ["height"] = image.Height,
                    ["quality"] = image.Quality,
                    ["byteCount"] = image.ByteCount
                })));
    }

    private static JsonArray BuildExportTelemetrySummaryArray(
        IReadOnlyList<LogReviewSession> sessions,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        return PayloadJson.CreateJsonArray(sessions
            .SelectMany(session =>
            {
                var channelMap = session.Snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
                return session.Snapshot.Metrics
                    .Where(sample => PayloadJson.MatchesTimestamp(sample.CapturedAtUtc, startUtc, endUtc))
                    .GroupBy(sample => sample.ChannelId)
                    .OrderBy(group => group.Key)
                    .Select(group =>
                    {
                        channelMap.TryGetValue(group.Key, out var channel);
                        var samples = group.OrderBy(sample => sample.CapturedAtUtc).ToArray();
                        return (JsonNode?)new JsonObject
                        {
                            ["sessionId"] = session.Snapshot.SessionId,
                            ["appId"] = session.Snapshot.AppId,
                            ["platformKey"] = session.PlatformKey,
                            ["channelId"] = group.Key,
                            ["name"] = channel?.Name ?? $"Channel {group.Key}",
                            ["type"] = PayloadJson.ResolveTelemetryType(group.Key, channelMap),
                            ["colorHex"] = channel?.ColorHex,
                            ["source"] = channel?.Source,
                            ["group"] = channel?.Group,
                            ["kind"] = channel?.Kind,
                            ["sampleCount"] = samples.Length,
                            ["firstSampleUtc"] = samples[0].CapturedAtUtc,
                            ["lastSampleUtc"] = samples[^1].CapturedAtUtc,
                            ["minimumValue"] = samples.Min(sample => sample.Value),
                            ["maximumValue"] = samples.Max(sample => sample.Value),
                            ["lastValue"] = samples[^1].Value
                        };
                    });
            }));
    }

    private static string BuildMarkdownLogSlice(
        IReadOnlyList<LogReviewSession> sessions,
        IReadOnlyList<SessionLogReviewEntry> entries,
        JsonArray annotations,
        JsonArray screenshots,
        JsonArray telemetrySummary,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int matchedLogCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Ansight Log Slice");
        builder.AppendLine();
        builder.AppendLine($"- Range: `{startUtc:O}` to `{endUtc:O}`");
        builder.AppendLine($"- Sessions: {sessions.Count}");
        builder.AppendLine($"- Logs: {entries.Count} returned of {matchedLogCount} matched");
        builder.AppendLine($"- Annotations: {annotations.Count}");
        builder.AppendLine($"- Screenshots: {screenshots.Count}");
        builder.AppendLine($"- Telemetry channels: {telemetrySummary.Count}");
        builder.AppendLine();

        builder.AppendLine("## Sessions");
        foreach (var session in sessions.OrderBy(session => session.Snapshot.AppId, StringComparer.Ordinal).ThenBy(session => session.Snapshot.SessionId, StringComparer.Ordinal))
        {
            builder.AppendLine($"- `{session.Snapshot.SessionId}` {session.Snapshot.AppId} {session.PlatformKey} {session.Snapshot.Status}");
        }

        builder.AppendLine();
        builder.AppendLine("## Logs");
        builder.AppendLine("| Time | Priority | Session | Source | Tag | Message |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var entry in entries)
        {
            builder.AppendLine(
                $"| {entry.Log.TimestampUtc:O} | {entry.Log.Priority} | {EscapeMarkdownCell(entry.Session.Snapshot.SessionId)} | {EscapeMarkdownCell(entry.Log.Source)} | {EscapeMarkdownCell(entry.Log.Tag)} | {EscapeMarkdownCell(TruncateLogMessage(entry.Log.Message, 240))} |");
        }

        if (annotations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Annotations");
            foreach (var node in annotations.OfType<JsonObject>())
            {
                builder.AppendLine(
                    $"- `{node["startUtc"]}` `{node["source"]}` {EscapeMarkdownCell(node["label"]?.GetValue<string>() ?? string.Empty)}");
            }
        }

        return builder.ToString();
    }

    private static bool TryReadPositiveInteger(
        JsonObject? arguments,
        string propertyName,
        int defaultValue,
        int maxValue,
        out int value,
        out string? errorMessage)
    {
        value = defaultValue;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out var parsedValue)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue)))
        {
            if (parsedValue <= 0)
            {
                errorMessage = $"'{propertyName}' must be greater than 0.";
                return false;
            }

            value = Math.Min(parsedValue, maxValue);
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    private static bool TryReadBoundedNonNegativeInteger(
        JsonObject? arguments,
        string propertyName,
        int defaultValue,
        int maxValue,
        out int value,
        out string? errorMessage)
    {
        value = defaultValue;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out var parsedValue)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue)))
        {
            if (parsedValue < 0)
            {
                errorMessage = $"'{propertyName}' must be 0 or greater.";
                return false;
            }

            value = Math.Min(parsedValue, maxValue);
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    private static bool TryReadOptionalIntegerArgument(JsonObject? arguments, string propertyName, out int? value, out string? errorMessage)
    {
        value = null;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue
            && (jsonValue.TryGetValue<int>(out var parsedValue)
                || int.TryParse(jsonValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue)))
        {
            value = parsedValue;
            return true;
        }

        errorMessage = $"'{propertyName}' must be an integer.";
        return false;
    }

    private static bool TryReadBooleanArgument(
        JsonObject? arguments,
        string propertyName,
        bool defaultValue,
        out bool value,
        out string? errorMessage)
    {
        value = defaultValue;
        errorMessage = null;
        if (arguments?[propertyName] is null)
        {
            return true;
        }

        if (arguments[propertyName] is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var parsedValue)
                || bool.TryParse(jsonValue.ToString(), out parsedValue))
            {
                value = parsedValue;
                return true;
            }
        }

        errorMessage = $"'{propertyName}' must be true or false.";
        return false;
    }

    private static bool TryReadExportFormat(JsonObject? arguments, out string format, out string? errorMessage)
    {
        format = "both";
        errorMessage = null;
        var rawFormat = NormalizeOptionalString(arguments?["format"]?.GetValue<string>());
        if (rawFormat is null)
        {
            return true;
        }

        if (string.Equals(rawFormat, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawFormat, "markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawFormat, "both", StringComparison.OrdinalIgnoreCase))
        {
            format = rawFormat.ToLowerInvariant();
            return true;
        }

        errorMessage = "format must be one of: json, markdown, both.";
        return false;
    }

    private static RequestResult CreateToolError(string message)
    {
        return RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = message
            },
            isError: true);
    }

    private static string NormalizeLogMessage(string message)
    {
        return string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string TruncateLogMessage(string message, int maxLength)
    {
        if (message.Length <= maxLength)
        {
            return message;
        }

        return string.Concat(message.AsSpan(0, Math.Max(0, maxLength - 3)), "...");
    }

    private static string EscapeMarkdownCell(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
