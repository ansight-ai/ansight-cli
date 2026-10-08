using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal sealed class LiveUiSelector
{
    private LiveUiSelector()
    {
    }

    public string? NodeId { get; private init; }

    public string? AutomationId { get; private init; }

    public string? Text { get; private init; }

    public string? Role { get; private init; }

    public string? Type { get; private init; }

    public string? AncestorAutomationId { get; private init; }

    public string? Action { get; private init; }

    public bool? Visible { get; private init; }

    public bool? Enabled { get; private init; }

    public LiveUiStringMatchMode MatchMode { get; private init; }

    public bool MatchModeSpecified { get; private init; }

    public bool Exact => MatchMode == LiveUiStringMatchMode.Exact;

    public bool CaseSensitive { get; private init; }

    public int Index { get; private init; }

    public bool IndexSpecified { get; private init; }

    public bool HasCriteria => NodeId is not null
                               || AutomationId is not null
                               || Text is not null
                               || Role is not null
                               || Type is not null
                               || AncestorAutomationId is not null
                               || Action is not null
                               || Visible.HasValue
                               || Enabled.HasValue;

    public static LiveUiSelector Parse(JsonObject? arguments, bool exactByDefault = true)
    {
        var defaultMatchMode = ReadBoolean(arguments, "exact", fallback: exactByDefault)
            ? LiveUiStringMatchMode.Exact
            : LiveUiStringMatchMode.Contains;
        return new LiveUiSelector
        {
            NodeId = ReadString(arguments, "nodeId"),
            AutomationId = ReadString(arguments, "automationId"),
            Text = ReadString(arguments, "text"),
            Role = ReadString(arguments, "role"),
            Type = ReadString(arguments, "type"),
            AncestorAutomationId = ReadString(arguments, "ancestorAutomationId"),
            Action = ReadString(arguments, "action"),
            Visible = ReadNullableBoolean(arguments, "visible"),
            Enabled = ReadNullableBoolean(arguments, "enabled"),
            MatchMode = ReadMatchMode(arguments, defaultMatchMode),
            MatchModeSpecified = ReadString(arguments, "matchMode") is not null,
            CaseSensitive = ReadBoolean(arguments, "caseSensitive", fallback: false),
            Index = Math.Max(0, ReadInteger(arguments, "index", fallback: 0)),
            IndexSpecified = arguments?.ContainsKey("index") == true
        };
    }

    public bool Matches(LiveUiNodeMatch candidate)
        => Evaluate(candidate).IsMatch;

    public LiveUiSelectorMatchResult Evaluate(LiveUiNodeMatch candidate)
    {
        var node = candidate.Node;
        var fieldMatches = new List<LiveUiSelectorFieldMatch>();
        AddFieldMatch(
            fieldMatches,
            "nodeId",
            EvaluateText(LiveUiNodeQuery.ReadString(node, "id"), NodeId, allowFuzzy: false));
        AddFieldMatch(
            fieldMatches,
            "automationId",
            EvaluateAutomationId(LiveUiNodeQuery.ReadAutomationId(node), AutomationId));
        AddFieldMatch(
            fieldMatches,
            "text",
            EvaluateText(LiveUiNodeQuery.ReadText(node), Text));
        AddFieldMatch(
            fieldMatches,
            "role",
            EvaluateText(LiveUiNodeQuery.ReadRole(node, candidate.TypeRegistry), Role));
        AddFieldMatch(
            fieldMatches,
            "type",
            EvaluateText(candidate.TypeRegistry.Resolve(node), Type));
        AddFieldMatch(fieldMatches, "ancestorAutomationId", EvaluateAncestor(candidate.Ancestors));
        AddFieldMatch(fieldMatches, "action", EvaluateAction(node, candidate.TypeRegistry));

        var stringCriteriaMatch = fieldMatches.All(static field => field.Result.IsMatch);
        var stateCriteriaMatch = (!Visible.HasValue
                                  || LiveUiNodeQuery.IsEffectivelyVisible(candidate) == Visible.Value)
                                 && (!Enabled.HasValue
                                     || LiveUiNodeQuery.IsEffectivelyEnabled(candidate) == Enabled.Value);
        if (!stringCriteriaMatch || !stateCriteriaMatch)
        {
            return LiveUiSelectorMatchResult.NoMatch;
        }

        var scoredFields = fieldMatches
            .Where(static field => field.Result.Applies)
            .ToArray();
        var score = scoredFields.Length == 0
            ? 1
            : Math.Round(scoredFields.Average(static field => field.Result.Score), 3);
        var reason = string.Join(
            ", ",
            scoredFields.Select(static field => $"{field.Name}:{field.Result.Reason}"));
        return new LiveUiSelectorMatchResult(true, score, reason);
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["nodeId"] = NodeId,
            ["automationId"] = AutomationId,
            ["text"] = Text,
            ["role"] = Role,
            ["type"] = Type,
            ["ancestorAutomationId"] = AncestorAutomationId,
            ["action"] = Action,
            ["visible"] = Visible,
            ["enabled"] = Enabled,
            ["exact"] = Exact,
            ["matchMode"] = MatchMode.ToString().ToLowerInvariant(),
            ["caseSensitive"] = CaseSensitive,
            ["index"] = Index,
            ["indexSpecified"] = IndexSpecified
        };
    }

    public string DescribeFailure(string message)
    {
        if (!HasCriteria) return message;
        var fields = ToJson();
        foreach (var field in fields.Where(static field => field.Value is null).Select(static field => field.Key).ToArray())
        {
            fields.Remove(field);
        }
        fields.Remove("indexSpecified");
        if (!IndexSpecified) fields.Remove("index");
        return $"{message} Selector: {fields.ToJsonString()}.";
    }

    public LiveUiSelector WithoutVisibility()
    {
        return new LiveUiSelector
        {
            NodeId = NodeId,
            AutomationId = AutomationId,
            Text = Text,
            Role = Role,
            Type = Type,
            AncestorAutomationId = AncestorAutomationId,
            Action = Action,
            Enabled = Enabled,
            MatchMode = MatchMode,
            MatchModeSpecified = MatchModeSpecified,
            CaseSensitive = CaseSensitive,
            Index = Index,
            IndexSpecified = IndexSpecified
        };
    }

    public bool CanUseOcr => Text is not null
                             && NodeId is null
                             && AutomationId is null
                             && Role is null
                             && Type is null
                             && AncestorAutomationId is null
                             && Action is null
                             && Enabled is null
                             && Visible != false;

    public IReadOnlyList<T> ApplyIndex<T>(IReadOnlyList<T> matches)
        => IndexSpecified
            ? matches.Skip(Index).Take(1).ToArray()
            : matches;

    public T? Select<T>(IReadOnlyList<T> matches)
        where T : class
        => matches.ElementAtOrDefault(Index);

    public bool IsAvailable(int matchCount)
        => matchCount > Index;

    public bool MatchesOcrText(string? actual)
        => EvaluateOcrText(actual).IsMatch;

    public LiveUiSelectorMatchResult EvaluateOcrText(string? actual)
    {
        if (Text is null || string.IsNullOrWhiteSpace(actual))
        {
            return LiveUiSelectorMatchResult.NoMatch;
        }

        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (MatchMode == LiveUiStringMatchMode.Contains)
        {
            return actual.Contains(Text, comparison)
                ? new LiveUiSelectorMatchResult(true, 1, "text:contains")
                : LiveUiSelectorMatchResult.NoMatch;
        }

        if (MatchMode == LiveUiStringMatchMode.Fuzzy)
        {
            var fuzzyMatch = LiveUiFuzzyStringMatcher.Evaluate(actual, Text, CaseSensitive);
            return fuzzyMatch.IsMatch
                ? new LiveUiSelectorMatchResult(
                    true,
                    fuzzyMatch.Score,
                    $"text:{fuzzyMatch.Reason}")
                : LiveUiSelectorMatchResult.NoMatch;
        }

        if (string.Equals(actual.Trim(), Text, comparison))
        {
            return new LiveUiSelectorMatchResult(true, 1, "text:exact");
        }

        if (MatchModeSpecified)
        {
            return LiveUiSelectorMatchResult.NoMatch;
        }

        var matchIndex = actual.IndexOf(Text, comparison);
        while (matchIndex >= 0)
        {
            var beforeIsBoundary = matchIndex == 0 || !char.IsLetterOrDigit(actual[matchIndex - 1]);
            var afterIndex = matchIndex + Text.Length;
            var afterIsBoundary = afterIndex == actual.Length || !char.IsLetterOrDigit(actual[afterIndex]);
            if (beforeIsBoundary && afterIsBoundary)
            {
                return new LiveUiSelectorMatchResult(true, 0.99, "text:phrase-boundary");
            }

            matchIndex = actual.IndexOf(Text, matchIndex + 1, comparison);
        }

        return LiveUiSelectorMatchResult.NoMatch;
    }

    private LiveUiStringFieldMatchResult EvaluateAncestor(IReadOnlyList<JsonObject> ancestors)
    {
        if (AncestorAutomationId is null)
        {
            return LiveUiStringFieldMatchResult.NotApplicable;
        }

        return ancestors
            .Select(ancestor => EvaluateAutomationId(
                LiveUiNodeQuery.ReadAutomationId(ancestor),
                AncestorAutomationId))
            .Where(static result => result.IsMatch)
            .OrderByDescending(static result => result.Score)
            .FirstOrDefault()
            ?? LiveUiStringFieldMatchResult.NoMatch;
    }

    private LiveUiStringFieldMatchResult EvaluateAutomationId(
        string? actual,
        string? expected)
    {
        if (MatchMode != LiveUiStringMatchMode.Exact
            || actual is null
            || expected is null)
        {
            return EvaluateText(actual, expected);
        }

        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(actual, expected, comparison)
            || AndroidResourceIdsAreEquivalent(actual, expected, comparison))
        {
            return new LiveUiStringFieldMatchResult(true, true, 1, "exact");
        }

        return LiveUiStringFieldMatchResult.NoMatch;
    }

    private static bool AndroidResourceIdsAreEquivalent(
        string actual,
        string expected,
        StringComparison comparison)
    {
        var actualResourceName = ReadAndroidResourceName(actual);
        var expectedResourceName = ReadAndroidResourceName(expected);
        if (actualResourceName is not null && expectedResourceName is not null)
        {
            return false;
        }

        return (actualResourceName is not null
                && IsUnqualifiedAutomationId(expected)
                && string.Equals(actualResourceName, expected, comparison))
               || (expectedResourceName is not null
                   && IsUnqualifiedAutomationId(actual)
                   && string.Equals(actual, expectedResourceName, comparison));
    }

    private static string? ReadAndroidResourceName(string automationId)
    {
        const string resourceMarker = ":id/";
        var markerIndex = automationId.IndexOf(resourceMarker, StringComparison.Ordinal);
        var nameIndex = markerIndex + resourceMarker.Length;
        return markerIndex > 0 && nameIndex < automationId.Length
            ? automationId[nameIndex..]
            : null;
    }

    private static bool IsUnqualifiedAutomationId(string automationId)
        => !automationId.Contains(':', StringComparison.Ordinal)
           && !automationId.Contains('/', StringComparison.Ordinal);

    private LiveUiStringFieldMatchResult EvaluateAction(
        JsonObject node,
        VisualTreeTypeRegistry typeRegistry)
    {
        if (Action is null)
        {
            return LiveUiStringFieldMatchResult.NotApplicable;
        }

        return LiveUiNodeQuery.ReadSupportedActions(node, typeRegistry)
            .Select(action => EvaluateText(action, Action))
            .Where(static result => result.IsMatch)
            .OrderByDescending(static result => result.Score)
            .FirstOrDefault()
            ?? LiveUiStringFieldMatchResult.NoMatch;
    }

    private LiveUiStringFieldMatchResult EvaluateText(
        string? actual,
        string? expected,
        bool allowFuzzy = true)
    {
        if (expected is null)
        {
            return LiveUiStringFieldMatchResult.NotApplicable;
        }

        if (actual is null)
        {
            return LiveUiStringFieldMatchResult.NoMatch;
        }

        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (MatchMode == LiveUiStringMatchMode.Exact || !allowFuzzy)
        {
            return string.Equals(actual, expected, comparison)
                ? new LiveUiStringFieldMatchResult(true, true, 1, "exact")
                : LiveUiStringFieldMatchResult.NoMatch;
        }

        if (MatchMode == LiveUiStringMatchMode.Contains)
        {
            return actual.Contains(expected, comparison)
                ? new LiveUiStringFieldMatchResult(true, true, 1, "contains")
                : LiveUiStringFieldMatchResult.NoMatch;
        }

        var fuzzyMatch = LiveUiFuzzyStringMatcher.Evaluate(actual, expected, CaseSensitive);
        return fuzzyMatch.IsMatch
            ? new LiveUiStringFieldMatchResult(
                true,
                true,
                fuzzyMatch.Score,
                fuzzyMatch.Reason)
            : LiveUiStringFieldMatchResult.NoMatch;
    }

    private static void AddFieldMatch(
        ICollection<LiveUiSelectorFieldMatch> fieldMatches,
        string name,
        LiveUiStringFieldMatchResult result)
    {
        if (result.Applies)
        {
            fieldMatches.Add(new LiveUiSelectorFieldMatch(name, result));
        }
    }

    private static string? ReadString(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool ReadBoolean(JsonObject? arguments, string propertyName, bool fallback)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : fallback;

    private static LiveUiStringMatchMode ReadMatchMode(
        JsonObject? arguments,
        LiveUiStringMatchMode fallback)
    {
        var value = ReadString(arguments, "matchMode");
        return value?.ToLowerInvariant() switch
        {
            "exact" => LiveUiStringMatchMode.Exact,
            "contains" => LiveUiStringMatchMode.Contains,
            "fuzzy" => LiveUiStringMatchMode.Fuzzy,
            _ => fallback
        };
    }

    private static bool? ReadNullableBoolean(JsonObject? arguments, string propertyName)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static int ReadInteger(JsonObject? arguments, string propertyName, int fallback)
        => arguments?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : fallback;
}

internal enum LiveUiStringMatchMode
{
    Exact,
    Contains,
    Fuzzy
}

internal sealed record LiveUiSelectorMatchResult(
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiSelectorMatchResult NoMatch { get; } = new(false, 0, "no-match");
}

internal sealed record LiveUiStringFieldMatchResult(
    bool Applies,
    bool IsMatch,
    double Score,
    string Reason)
{
    public static LiveUiStringFieldMatchResult NotApplicable { get; } = new(false, true, 1, "not-applicable");

    public static LiveUiStringFieldMatchResult NoMatch { get; } = new(true, false, 0, "no-match");
}

internal sealed record LiveUiSelectorFieldMatch(
    string Name,
    LiveUiStringFieldMatchResult Result);
