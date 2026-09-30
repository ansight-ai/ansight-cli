using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal sealed class ExtractExceptionsTool : Operation
{
    private static readonly Regex exceptionTypePattern = new(@"\b([A-Za-z_][A-Za-z0-9_.]*Exception)\b", RegexOptions.Compiled);
    private static readonly Regex stackFramePattern = new(@"^\s*(at\s+|--- End|Caused by:|at\s+[A-Za-z0-9_.$<>]+\()", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private readonly LogReviewActions logReview;

    public ExtractExceptionsTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_extract_exceptions";

    protected override string Title => "Extract Exceptions";

    protected override string Description => "Find and group exception-like log entries, stack traces, crash markers, and fatal/error clusters across captured sessions.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: BuildSchemaProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!logReview.TryResolveLogReviewSessions(arguments, out var sessions, out var sessionFilters, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Unable to resolve sessions."));
        }

        if (!LogReviewActions.TryReadLogReviewFilters(arguments, out var filters, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                OperationDefaults.DefaultExceptionGroupLimit,
                OperationDefaults.MaxExceptionGroupLimit,
                out var limit,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid exception extraction filters."));
        }

        var candidates = logReview.GetFilteredLogEntries(sessions, filters)
            .Select(TryBuildCandidate)
            .Where(candidate => candidate is not null)
            .Cast<ExceptionCandidate>()
            .ToArray();
        var groups = candidates
            .GroupBy(candidate => candidate.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildExceptionGroupPayload(group))
            .OrderByDescending(group => group.Count)
            .ThenByDescending(group => group.MaximumPriority)
            .ThenBy(group => group.FirstUtc)
            .Take(limit)
            .Select(group => (JsonNode?)group.Payload);

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["filters"] = LogReviewActions.BuildLogReviewFiltersPayload(sessionFilters, filters),
                ["searchedSessionCount"] = sessions.Count,
                ["candidateLogCount"] = candidates.Length,
                ["returnedGroupLimit"] = limit,
                ["exceptionGroups"] = PayloadJson.CreateJsonArray(groups)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> BuildSchemaProperties()
    {
        var properties = SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: false);
        properties["limit"] = ToolSchema.Integer("Maximum number of exception groups to return. Defaults to 50, max 200.", nullable: true);
        return properties;
    }

    private static ExceptionCandidate? TryBuildCandidate(SessionLogReviewEntry entry)
    {
        var message = entry.Log.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var exceptionType = ResolveExceptionType(message, entry.Log.Priority);
        if (exceptionType is null)
        {
            return null;
        }

        var summary = ResolveSummary(message);
        return new ExceptionCandidate(
            entry,
            exceptionType,
            summary,
            BuildGroupKey(exceptionType, summary),
            CountStackFrames(message));
    }

    private static ExceptionGroup BuildExceptionGroupPayload(IGrouping<string, ExceptionCandidate> group)
    {
        var orderedCandidates = group
            .OrderBy(candidate => candidate.Entry.Log.TimestampUtc)
            .ThenBy(candidate => candidate.Entry.Session.Snapshot.SessionId, StringComparer.Ordinal)
            .ToArray();
        var first = orderedCandidates[0];
        var maximumPriority = orderedCandidates.Max(candidate => candidate.Entry.Log.Priority);
        var sessionIds = orderedCandidates
            .Select(candidate => candidate.Entry.Session.Snapshot.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var payload = new JsonObject
        {
            ["exceptionType"] = first.ExceptionType,
            ["summary"] = first.Summary,
            ["count"] = orderedCandidates.Length,
            ["sessionCount"] = sessionIds.Length,
            ["firstUtc"] = orderedCandidates[0].Entry.Log.TimestampUtc,
            ["lastUtc"] = orderedCandidates[^1].Entry.Log.TimestampUtc,
            ["maximumPriority"] = maximumPriority.ToString(),
            ["stackFrameCount"] = orderedCandidates.Max(candidate => candidate.StackFrameCount),
            ["sessions"] = PayloadJson.CreateJsonArray(sessionIds.Select(sessionId => JsonValue.Create(sessionId))),
            ["sampleLogs"] = PayloadJson.CreateJsonArray(orderedCandidates
                .Take(5)
                .Select(candidate => (JsonNode?)LogReviewActions.BuildSessionLogEntryPayload(candidate.Entry, isTarget: false)))
        };

        return new ExceptionGroup(payload, orderedCandidates.Length, maximumPriority, orderedCandidates[0].Entry.Log.TimestampUtc);
    }

    private static string? ResolveExceptionType(string message, LogPriority priority)
    {
        var exceptionMatch = exceptionTypePattern.Match(message);
        if (exceptionMatch.Success)
        {
            return exceptionMatch.Groups[1].Value;
        }

        if (message.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase))
        {
            return "Fatal Exception";
        }

        if (message.Contains("unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Unhandled Exception";
        }

        if (message.Contains("SIGABRT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SIGSEGV", StringComparison.OrdinalIgnoreCase)
            || message.Contains("EXC_BAD_ACCESS", StringComparison.OrdinalIgnoreCase))
        {
            return "Native Crash";
        }

        if (priority >= LogPriority.Error && stackFramePattern.IsMatch(message))
        {
            return "Error Stack Trace";
        }

        return null;
    }

    private static string ResolveSummary(string message)
    {
        var line = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => !stackFramePattern.IsMatch(value))
            ?? message.Trim();
        return line.Length <= 500 ? line : string.Concat(line.AsSpan(0, 497), "...");
    }

    private static string BuildGroupKey(string exceptionType, string summary)
        => $"{exceptionType}|{NormalizeExceptionSummary(summary)}";

    private static string NormalizeExceptionSummary(string summary)
        => Regex.Replace(summary, @"0x[0-9a-fA-F]+|\b\d+\b|[0-9a-fA-F]{8,}", "#").Trim();

    private static int CountStackFrames(string message)
        => stackFramePattern.Matches(message).Count;

}
