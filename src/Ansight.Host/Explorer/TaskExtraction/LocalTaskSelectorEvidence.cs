using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Sanitization;

namespace Ansight.Host.Explorer.TaskExtraction;

internal static class LocalTaskSelectorEvidence
{
    private static readonly IReadOnlySet<string> selectorFieldNames = new HashSet<string>(
        [
            "nodeId",
            "automationId",
            "text",
            "role",
            "type",
            "ancestorAutomationId",
            "action"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> selectorCallToolNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ansight.keyboard.open"] = "ansight_open_keyboard",
            ["ansight.ui.assert"] = "ansight_assert_ui",
            ["ansight.ui.find"] = "ansight_find_ui",
            ["ansight.ui.pinch"] = "ansight_pinch_ui",
            ["ansight.ui.scroll"] = "ansight_scroll_ui",
            ["ansight.ui.swipe"] = "ansight_swipe_ui",
            ["ansight.ui.tap"] = "ansight_tap_ui",
            ["ansight.ui.typeText"] = "ansight_type_text",
            ["ansight.ui.waitFor"] = "ansight_wait_for_ui"
        };

    public static LocalTaskSelectorEvidenceIndex Create(
        IEnumerable<SessionVisualTreeSnapshot> visualTrees,
        int screenshotFrameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(visualTrees);
        var trees = visualTrees.OrderBy(static tree => tree.CapturedAtUtc).ToArray();
        var nodes = new List<LocalTaskSelectorEvidenceNode>();
        foreach (var tree in trees)
        {
            if (tree.Payload["root"] is JsonObject root)
            {
                var typeRegistry = VisualTreeTypeRegistry.FromPayload(tree.Payload);
                foreach (var match in LiveUiNodeQuery.Enumerate(root, typeRegistry)
                             .Where(LiveUiNodeQuery.IsEffectivelyVisible))
                {
                    var textValues = new HashSet<string>(ReadTextValues(match.Node), StringComparer.OrdinalIgnoreCase);
                    if (LiveUiNodeQuery.ReadText(match.Node) is { } primaryText)
                    {
                        textValues.Add(primaryText);
                    }

                    nodes.Add(new LocalTaskSelectorEvidenceNode(
                        LiveUiNodeQuery.ReadString(match.Node, "id"),
                        LiveUiNodeQuery.ReadAutomationId(match.Node),
                        LiveUiNodeQuery.ReadRole(match.Node, typeRegistry),
                        typeRegistry.Resolve(match.Node),
                        textValues,
                        LiveUiNodeQuery.ReadSupportedActions(match.Node, typeRegistry)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        match.Ancestors
                            .Select(LiveUiNodeQuery.ReadAutomationId)
                            .Where(static value => !string.IsNullOrWhiteSpace(value))
                            .Select(static value => value!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase),
                        tree.SnapshotId,
                        tree.CapturedAtUtc,
                        tree.VisualTreeKind,
                        tree.Source));
                }
            }
        }

        return new LocalTaskSelectorEvidenceIndex(trees.Length, screenshotFrameCount, 0, nodes);
    }

    public static IReadOnlyList<LocalTaskSelectorCall> ExtractSelectorCalls(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = Tokenize(source);
        var calls = new List<LocalTaskSelectorCall>();
        for (var index = 0; index + 5 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier
                || !string.Equals(tokens[index].Text, "ansight", StringComparison.Ordinal)
                || tokens[index + 1].Text != "."
                || !tokens[index + 2].IsIdentifier
                || tokens[index + 3].Text != "."
                || !tokens[index + 4].IsIdentifier
                || tokens[index + 5].Text != "(")
            {
                continue;
            }

            var callPath = $"ansight.{tokens[index + 2].Text}.{tokens[index + 4].Text}";
            if (!selectorCallToolNames.TryGetValue(callPath, out var toolName))
            {
                continue;
            }

            var argumentIndex = index + 6;
            if (argumentIndex >= tokens.Count || tokens[argumentIndex].Text != "{")
            {
                calls.Add(new LocalTaskSelectorCall(
                    calls.Count + 1,
                    callPath,
                    toolName,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    [],
                    IsStaticallyInspectable: false));
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var dynamicFields = new HashSet<string>(StringComparer.Ordinal);
            var hasDynamicObjectShape = false;
            var depth = 0;
            for (var cursor = argumentIndex; cursor < tokens.Count; cursor++)
            {
                var token = tokens[cursor];
                if (token.Text == "{")
                {
                    depth++;
                    continue;
                }
                if (token.Text == "}")
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }

                    continue;
                }
                if (depth != 1)
                {
                    continue;
                }

                var isPropertyStart = cursor == argumentIndex + 1
                                      || tokens[cursor - 1].Text is "{" or ",";
                if (!isPropertyStart)
                {
                    continue;
                }

                if (token.Text == "["
                    || (token.Text == "."
                        && cursor + 2 < tokens.Count
                        && tokens[cursor + 1].Text == "."
                        && tokens[cursor + 2].Text == "."))
                {
                    hasDynamicObjectShape = true;
                    continue;
                }
                if (!TryReadPropertyName(token, out var propertyName)
                    || !selectorFieldNames.Contains(propertyName))
                {
                    continue;
                }
                if (cursor + 1 >= tokens.Count || tokens[cursor + 1].Text != ":")
                {
                    dynamicFields.Add(propertyName);
                    continue;
                }
                if (cursor + 2 >= tokens.Count)
                {
                    dynamicFields.Add(propertyName);
                    continue;
                }

                var value = tokens[cursor + 2];
                if (value.Kind == LocalTaskSourceTokenKind.String)
                {
                    fields[propertyName] = value.Text;
                }
                else
                {
                    dynamicFields.Add(propertyName);
                }
            }

            calls.Add(new LocalTaskSelectorCall(
                calls.Count + 1,
                callPath,
                toolName,
                fields,
                dynamicFields.Order(StringComparer.Ordinal).ToArray(),
                IsStaticallyInspectable: !hasDynamicObjectShape));
        }

        return calls;
    }

    public static IReadOnlyList<LocalTaskSelectorGroundingIssue> Validate(
        string source,
        LocalTaskSelectorEvidenceIndex evidence)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(evidence);
        var issues = new List<LocalTaskSelectorGroundingIssue>();
        foreach (var call in ExtractSelectorCalls(source))
        {
            if (!call.IsStaticallyInspectable)
            {
                issues.Add(new LocalTaskSelectorGroundingIssue(
                    call,
                    $"UI selector in {call.CallPath} call {call.Sequence:N0} is not an inline object and cannot be validated against captured UI evidence."));
                continue;
            }
            if (call.DynamicFields.Count > 0)
            {
                issues.Add(new LocalTaskSelectorGroundingIssue(
                    call,
                    $"UI selector in {call.CallPath} call {call.Sequence:N0} uses non-literal {FormatFieldList(call.DynamicFields)} and cannot be proven from captured UI evidence."));
                continue;
            }
            if (call.Fields.Count == 0)
            {
                continue;
            }
            if (evidence.TreeCount == 0 && evidence.OcrFrameCount == 0)
            {
                issues.Add(new LocalTaskSelectorGroundingIssue(
                    call,
                    $"UI selector in {call.CallPath} call {call.Sequence:N0} cannot be validated because the selected period contains no readable visual-tree or OCR evidence."));
                continue;
            }
            if (!evidence.Nodes.Any(node => Matches(node, call.Fields)))
            {
                issues.Add(new LocalTaskSelectorGroundingIssue(
                    call,
                    $"UI selector in {call.CallPath} call {call.Sequence:N0} was not observed in the selected visual-tree or OCR evidence: {FormatSelector(call.Fields)}."));
            }
        }

        return issues;
    }

    public static IReadOnlyList<LocalTaskExtractionSuggestedSelector> SuggestSelectors(
        LocalTaskSelectorCall failedCall,
        LocalTaskSelectorEvidenceIndex before,
        LocalTaskSelectorEvidenceIndex after)
    {
        ArgumentNullException.ThrowIfNull(failedCall);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var searchTerms = failedCall.Fields.Values
            .SelectMany(SplitSearchTerms)
            .Where(static value => value.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<LocalTaskSuggestedSelectorCandidate>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in after.Nodes.Where(node => !before.ContainsIdentity(node)))
        {
            if (!string.IsNullOrWhiteSpace(node.AutomationId))
            {
                var selector = new JsonObject { ["automationId"] = node.AutomationId };
                var key = selector.ToJsonString();
                if (keys.Add(key))
                {
                    candidates.Add(new LocalTaskSuggestedSelectorCandidate(
                        Score(node.AutomationId, searchTerms, 200),
                        new LocalTaskExtractionSuggestedSelector(
                            selector,
                            $"Observed after the preceding action in the {DescribeTree(node)} tree.",
                            node.SnapshotId,
                            node.CapturedAtUtc)));
                }
            }

            foreach (var text in node.TextValues.Where(IsUsefulText))
            {
                var selector = new JsonObject { ["text"] = text };
                if (!string.IsNullOrWhiteSpace(node.Role))
                {
                    selector["role"] = node.Role;
                }
                var key = selector.ToJsonString();
                if (!keys.Add(key))
                {
                    continue;
                }

                candidates.Add(new LocalTaskSuggestedSelectorCandidate(
                    Score(text, searchTerms, 100),
                    new LocalTaskExtractionSuggestedSelector(
                        selector,
                        $"Visible text observed after the preceding action in the {DescribeTree(node)} tree.",
                        node.SnapshotId,
                        node.CapturedAtUtc)));
            }
        }

        return candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Suggestion.Selector.ToJsonString(), StringComparer.Ordinal)
            .Take(5)
            .Select(static candidate => candidate.Suggestion)
            .ToArray();
    }

    public static bool Matches(
        LocalTaskSelectorEvidenceNode node,
        IReadOnlyDictionary<string, string> fields)
    {
        foreach (var field in fields)
        {
            var matches = field.Key switch
            {
                "nodeId" => EqualsValue(node.NodeId, field.Value),
                "automationId" => EqualsValue(node.AutomationId, field.Value),
                "text" => node.TextValues.Contains(field.Value),
                "role" => EqualsValue(node.Role, field.Value),
                "type" => EqualsValue(node.Type, field.Value),
                "ancestorAutomationId" => node.AncestorAutomationIds.Contains(field.Value),
                "action" => node.Actions.Contains(field.Value),
                _ => true
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanUseOcr(LocalTaskSelectorCall call)
    {
        if (!call.IsStaticallyInspectable
            || call.DynamicFields.Count > 0
            || !call.Fields.ContainsKey("text"))
        {
            return false;
        }

        foreach (var field in call.Fields)
        {
            if (field.Key == "text")
            {
                continue;
            }
            if (field.Key == "role"
                && string.Equals(field.Value, "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static string FormatSelector(IReadOnlyDictionary<string, string> fields)
        => string.Join(
            ", ",
            fields.OrderBy(static field => field.Key, StringComparer.Ordinal)
                .Select(static field => $"{field.Key}={JsonValue.Create(field.Value)!.ToJsonString()}"));

    private static IReadOnlySet<string> ReadTextValues(JsonObject node)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddString(values, node, "text");
        AddString(values, node, "label");
        AddString(values, node, "title");
        AddString(values, node, "value");
        if (node["visual"] is JsonObject visual)
        {
            AddString(values, visual, "text");
            AddString(values, visual, "value");
        }
        if (node["properties"] is JsonObject properties)
        {
            AddString(values, properties, "text");
            AddString(values, properties, "value");
            AddString(values, properties, "placeholder");
        }

        return values;
    }

    private static string? ReadString(JsonObject value, string propertyName)
        => ReadStringValue(value[propertyName]);

    private static string? ReadStringValue(JsonNode? value)
        => value is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static void AddString(ISet<string> values, JsonObject source, string propertyName)
    {
        var value = ReadString(source, propertyName);
        if (value is not null)
        {
            values.Add(value);
        }
    }

    private static bool TryReadPropertyName(LocalTaskSourceToken token, out string propertyName)
    {
        if (token.IsIdentifier || token.Kind == LocalTaskSourceTokenKind.String)
        {
            propertyName = token.Text;
            return true;
        }

        propertyName = string.Empty;
        return false;
    }

    private static IReadOnlyList<LocalTaskSourceToken> Tokenize(string source)
    {
        var tokens = new List<LocalTaskSourceToken>();
        for (var index = 0; index < source.Length;)
        {
            var character = source[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n')
                {
                    index++;
                }
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length
                       && (source[index] != '*' || source[index + 1] != '/'))
                {
                    index++;
                }
                index = Math.Min(source.Length, index + 2);
                continue;
            }
            if (character is '\'' or '"' or '`')
            {
                tokens.Add(ReadQuotedToken(source, ref index, character));
                continue;
            }
            if (IsIdentifierStart(character))
            {
                var end = index + 1;
                while (end < source.Length && IsIdentifierPart(source[end]))
                {
                    end++;
                }
                tokens.Add(new LocalTaskSourceToken(
                    source[index..end],
                    LocalTaskSourceTokenKind.Identifier));
                index = end;
                continue;
            }

            tokens.Add(new LocalTaskSourceToken(
                character.ToString(),
                LocalTaskSourceTokenKind.Punctuation));
            index++;
        }

        return tokens;
    }

    private static LocalTaskSourceToken ReadQuotedToken(
        string source,
        ref int index,
        char delimiter)
    {
        var builder = new StringBuilder();
        var dynamic = false;
        index++;
        while (index < source.Length)
        {
            var character = source[index++];
            if (character == delimiter)
            {
                break;
            }
            if (delimiter == '`'
                && character == '$'
                && index < source.Length
                && source[index] == '{')
            {
                dynamic = true;
            }
            if (character != '\\' || index >= source.Length)
            {
                builder.Append(character);
                continue;
            }

            var escaped = source[index++];
            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => escaped
            });
        }

        return new LocalTaskSourceToken(
            builder.ToString(),
            dynamic ? LocalTaskSourceTokenKind.DynamicString : LocalTaskSourceTokenKind.String);
    }

    private static IEnumerable<string> SplitSearchTerms(string value)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character))
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
                continue;
            }
            if (builder.Length > 0
                && char.IsUpper(character)
                && char.IsLower(builder[^1]))
            {
                yield return builder.ToString();
                builder.Clear();
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool IsUsefulText(string value)
        => value.Length is > 1 and <= 120 && value.Any(char.IsLetterOrDigit);

    private static int Score(string value, IReadOnlySet<string> searchTerms, int baseline)
    {
        var score = baseline;
        foreach (var term in searchTerms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }
        }

        return score;
    }

    private static bool EqualsValue(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string FormatFieldList(IReadOnlyList<string> fields)
        => fields.Count == 1
            ? $"selector field '{fields[0]}'"
            : $"selector fields {string.Join(", ", fields.Select(static field => $"'{field}'"))}";

    private static string DescribeTree(LocalTaskSelectorEvidenceNode node)
        => string.IsNullOrWhiteSpace(node.VisualTreeKind)
            ? node.Source
            : $"{node.VisualTreeKind}/{node.Source}";

    private static bool IsIdentifierStart(char character)
        => char.IsAsciiLetter(character) || character is '_' or '$';

    private static bool IsIdentifierPart(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '_' or '$';

    private enum LocalTaskSourceTokenKind
    {
        Identifier,
        String,
        DynamicString,
        Punctuation
    }

    private sealed record LocalTaskSourceToken(string Text, LocalTaskSourceTokenKind Kind)
    {
        public bool IsIdentifier => Kind == LocalTaskSourceTokenKind.Identifier;
    }

    private sealed record LocalTaskSuggestedSelectorCandidate(
        int Score,
        LocalTaskExtractionSuggestedSelector Suggestion);
}
