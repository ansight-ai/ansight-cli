using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal static class SessionReviewContext
{
    public static bool TryResolveReviewSession(
        SessionResolver sessionResolver,
        JsonObject? arguments,
        out AppSessionSnapshot? snapshot,
        out string? errorMessage)
    {
        snapshot = null;
        errorMessage = null;
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var resolvedSnapshot, out var resolutionError))
        {
            errorMessage = resolutionError;
            return false;
        }

        var platformFilters = SessionPlatformFilters.Read(arguments);
        var platformKey = SessionPlatformFilters.ResolveSessionPlatformKey(resolvedSnapshot!);
        if (platformFilters.Count > 0 && !platformFilters.Contains(platformKey))
        {
            errorMessage = $"Session '{resolvedSnapshot!.SessionId}' platform '{platformKey}' does not match the requested platform filters.";
            return false;
        }

        snapshot = resolvedSnapshot;
        return true;
    }

    public static bool TryReadTimeRange(JsonObject? arguments, out DateTimeOffset? startUtc, out DateTimeOffset? endUtc, out string? errorMessage)
    {
        startUtc = null;
        endUtc = null;
        errorMessage = null;
        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out startUtc, out errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out endUtc, out errorMessage))
        {
            return false;
        }

        if (startUtc.HasValue && endUtc.HasValue && startUtc.Value > endUtc.Value)
        {
            errorMessage = "startUtc must be before or equal to endUtc.";
            return false;
        }

        return true;
    }

    public static IndexedReviewLog[] BuildIndexedReviewLogs(AppSessionSnapshot snapshot)
    {
        return snapshot.Logs
            .Select((log, originalIndex) => new IndexedReviewLog(originalIndex, log))
            .OrderBy(entry => entry.Log.TimestampUtc)
            .ThenBy(entry => entry.Index)
            .Select((entry, sortedIndex) => new IndexedReviewLog(sortedIndex, entry.Log))
            .ToArray();
    }

    public static DateTimeOffset? ResolveTargetTimestamp(
        AppSessionSnapshot snapshot,
        JsonObject? arguments,
        out JsonObject? targetPayload,
        out string? errorMessage)
    {
        targetPayload = null;
        if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage)
            || !ArgumentReader.TryReadOptionalIntegerArgument(arguments, "logIndex", out var logIndex, out errorMessage))
        {
            return null;
        }

        var eventId = NormalizeOptionalString(arguments?["eventId"]?.GetValue<string>());
        var logs = BuildIndexedReviewLogs(snapshot);
        IndexedReviewLog? targetLog = null;
        if (logIndex.HasValue)
        {
            if (logIndex.Value < 0 || logIndex.Value >= logs.Length)
            {
                errorMessage = logs.Length == 0
                    ? "logIndex cannot be used because the session has no logs."
                    : $"logIndex must be between 0 and {logs.Length - 1}.";
                return null;
            }

            targetLog = logs[logIndex.Value];
        }
        else if (eventId is not null)
        {
            targetLog = logs.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.Log.EventId)
                && string.Equals(entry.Log.EventId, eventId, StringComparison.OrdinalIgnoreCase));
            if (targetLog is null)
            {
                errorMessage = $"No log with eventId '{eventId}' was found.";
                return null;
            }
        }

        if (targetLog is not null)
        {
            targetPayload = BuildLogTargetPayload(targetLog);
            return targetLog.Log.TimestampUtc;
        }

        if (!timestampUtc.HasValue)
        {
            errorMessage = "One of timestampUtc, logIndex, or eventId is required.";
            return null;
        }

        targetPayload = new JsonObject
        {
            ["kind"] = "timestamp",
            ["timestampUtc"] = timestampUtc.Value
        };
        errorMessage = null;
        return timestampUtc.Value;
    }

    public static JsonObject BuildLogTargetPayload(IndexedReviewLog entry)
    {
        return new JsonObject
        {
            ["kind"] = "log",
            ["logIndex"] = entry.Index,
            ["timestampUtc"] = entry.Log.TimestampUtc,
            ["priority"] = entry.Log.Priority.ToString(),
            ["source"] = entry.Log.Source,
            ["tag"] = entry.Log.Tag,
            ["eventId"] = entry.Log.EventId,
            ["message"] = entry.Log.Message
        };
    }

    public static long CalculateDeltaMilliseconds(DateTimeOffset timestampUtc, DateTimeOffset targetUtc)
        => (long)Math.Abs((timestampUtc - targetUtc).TotalMilliseconds);

    public static JsonObject BuildSessionHeaderPayload(AppSessionSnapshot snapshot, bool isLive)
    {
        var payload = PayloadJson.BuildSessionPayload(snapshot, isLive);
        payload["platformKey"] = SessionPlatformFilters.ResolveSessionPlatformKey(snapshot);
        return payload;
    }

    public static bool IsLiveSession(SessionResolver sessionResolver, AppSessionSnapshot snapshot)
        => sessionResolver.GetLiveSessionIds().Contains(snapshot.SessionId);

    private static string? NormalizeOptionalString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
