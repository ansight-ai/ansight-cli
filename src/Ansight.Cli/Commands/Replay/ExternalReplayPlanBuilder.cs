using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.Replay;

internal static class ExternalReplayPlanBuilder
{
    private const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int MaximumLabelCharacters = 120;

    public static async Task<ReplayPlan> BuildFromFileAsync(
        string provider,
        string filePath,
        string? appId,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new CliUsageException($"Replay export file '{fullPath}' was not found.");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new CliUsageException(
                $"Replay export file '{fullPath}' is {fileInfo.Length:N0} bytes; "
                + $"the maximum supported size is {MaximumFileBytes:N0} bytes.");
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var root = ParseDocument(content, fullPath);
        return Build(normalizedProvider, Path.GetFileName(fullPath), appId, root);
    }

    internal static ReplayPlan Build(
        string provider,
        string sourceId,
        string? appId,
        JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var normalizedProvider = NormalizeProvider(provider);
        var replayEvents = new List<ExternalReplayEvent>();
        CollectReplayEvents(root, replayEvents);
        replayEvents.Sort(ExternalReplayEventComparer.Instance);

        var diagnostics = new List<string>();
        var nodes = new Dictionary<int, ExternalReplayNode>();
        var actions = new List<ExternalReplayAction>();
        var fallbackActions = new List<ExternalReplayAction>();
        var scrollOffsets = new Dictionary<int, double>();
        string? initialRoute = null;
        foreach (var replayEvent in replayEvents)
        {
            switch (replayEvent.Type)
            {
                case 2:
                    if (replayEvent.Data["node"] is JsonObject rootNode)
                    {
                        AddNodeTree(rootNode, nodes, parentId: null);
                    }
                    break;
                case 3:
                    ProcessIncrementalSnapshot(
                        replayEvent,
                        nodes,
                        scrollOffsets,
                        actions,
                        diagnostics);
                    break;
                case 4:
                    initialRoute ??= NormalizeRoute(ReadString(replayEvent.Data, "href"));
                    break;
                case 5:
                    ProcessCustomEvent(replayEvent, fallbackActions);
                    break;
            }
        }

        if (actions.Count == 0)
        {
            actions.AddRange(fallbackActions);
        }

        if (actions.Count == 0)
        {
            AddPostHogAutocaptureFallback(root, actions);
        }

        var compactedActions = CompactActions(actions);
        if (initialRoute is not null)
        {
            compactedActions.Insert(
                0,
                new ExternalReplayAction(
                    "navigate",
                    $"Navigate to the app screen corresponding to the recorded route {JsonSerializer.Serialize(initialRoute)}.",
                    CapturedAtUtc: null,
                    Target: initialRoute,
                    NodeId: null));
        }

        if (compactedActions.Count > AnsightReplayPlanner.MaximumReplaySteps)
        {
            diagnostics.Add(
                $"The {normalizedProvider} export produced {compactedActions.Count:N0} replayable actions; "
                + $"only the first {AnsightReplayPlanner.MaximumReplaySteps:N0} can be sent to one agent run.");
            compactedActions = compactedActions
                .Take(AnsightReplayPlanner.MaximumReplaySteps)
                .ToList();
        }

        if (replayEvents.Count == 0)
        {
            diagnostics.Add(
                $"The {normalizedProvider} export contained no recognized RRWeb replay events.");
        }
        else if (compactedActions.Count == 0)
        {
            diagnostics.Add(
                $"The {normalizedProvider} export contained replay frames but no supported click, input, scroll, or navigation actions.");
        }

        var steps = compactedActions
            .Select((action, index) => new ReplayStep(
                index + 1,
                action.Kind,
                action.Instruction,
                action.CapturedAtUtc,
                action.Target))
            .ToArray();
        return new ReplayPlan(
            "ansight.replay-plan/v1",
            normalizedProvider,
            sourceId,
            NormalizeOptional(appId),
            replayEvents.Count,
            steps,
            diagnostics);
    }

    private static JsonNode ParseDocument(string content, string sourcePath)
    {
        try
        {
            return JsonNode.Parse(
                       content,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = true,
                           CommentHandling = JsonCommentHandling.Skip
                       })
                   ?? throw new CliUsageException($"Replay export file '{sourcePath}' is empty.");
        }
        catch (JsonException documentException)
        {
            var lines = new JsonArray();
            try
            {
                foreach (var line in content.Split('\n'))
                {
                    var normalizedLine = line.Trim();
                    if (normalizedLine.Length > 0)
                    {
                        lines.Add(JsonNode.Parse(normalizedLine));
                    }
                }
            }
            catch (JsonException)
            {
                throw new CliUsageException(
                    $"Replay export file '{sourcePath}' is not valid JSON or JSON Lines: {documentException.Message}");
            }

            if (lines.Count == 0)
            {
                throw new CliUsageException($"Replay export file '{sourcePath}' is empty.");
            }

            return lines;
        }
    }

    private static void CollectReplayEvents(JsonNode? node, ICollection<ExternalReplayEvent> events)
    {
        if (node is JsonObject value)
        {
            if (TryReadInteger(value, "type", out var type)
                && TryReadTimestamp(value["timestamp"], out var timestampUtc)
                && value["data"] is JsonObject data)
            {
                events.Add(new ExternalReplayEvent(type, timestampUtc, data));
                return;
            }

            foreach (var property in value)
            {
                CollectReplayEvents(property.Value, events);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectReplayEvents(item, events);
            }

            return;
        }

        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && text.Length is > 1 and < 16_777_216
            && (text[0] == '{' || text[0] == '['))
        {
            try
            {
                CollectReplayEvents(JsonNode.Parse(text), events);
            }
            catch (JsonException)
            {
                // Provider exports can contain unrelated strings that happen to begin with JSON punctuation.
            }
        }
    }

    private static void ProcessIncrementalSnapshot(
        ExternalReplayEvent replayEvent,
        Dictionary<int, ExternalReplayNode> nodes,
        IDictionary<int, double> scrollOffsets,
        ICollection<ExternalReplayAction> actions,
        ICollection<string> diagnostics)
    {
        if (!TryReadInteger(replayEvent.Data, "source", out var source))
        {
            return;
        }

        switch (source)
        {
            case 0:
                ApplyMutations(replayEvent.Data, nodes);
                break;
            case 2:
                AddPointerAction(replayEvent, nodes, actions, diagnostics);
                break;
            case 3:
                AddScrollAction(replayEvent, nodes, scrollOffsets, actions);
                break;
            case 5:
                AddInputAction(replayEvent, nodes, actions, diagnostics);
                break;
        }
    }

    private static void ApplyMutations(
        JsonObject data,
        IDictionary<int, ExternalReplayNode> nodes)
    {
        if (data["removes"] is JsonArray removes)
        {
            foreach (var remove in removes.OfType<JsonObject>())
            {
                if (TryReadInteger(remove, "id", out var removedId))
                {
                    nodes.Remove(removedId);
                }
            }
        }

        if (data["adds"] is JsonArray adds)
        {
            foreach (var add in adds.OfType<JsonObject>())
            {
                int? parentId = TryReadInteger(add, "parentId", out var value) ? value : null;
                if (add["node"] is JsonObject addedNode)
                {
                    AddNodeTree(addedNode, nodes, parentId);
                }
            }
        }

        if (data["texts"] is JsonArray texts)
        {
            foreach (var text in texts.OfType<JsonObject>())
            {
                if (TryReadInteger(text, "id", out var id)
                    && nodes.TryGetValue(id, out var existing))
                {
                    var nextText = NormalizeLabel(ReadString(text, "value"));
                    nodes[id] = existing with { Text = nextText };
                    if (nextText is not null
                        && existing.ParentId.HasValue
                        && nodes.TryGetValue(existing.ParentId.Value, out var parent)
                        && parent.Text is null)
                    {
                        nodes[parent.Id] = parent with { Text = nextText };
                    }
                }
            }
        }

        if (data["attributes"] is JsonArray attributes)
        {
            foreach (var attributeMutation in attributes.OfType<JsonObject>())
            {
                if (!TryReadInteger(attributeMutation, "id", out var id)
                    || !nodes.TryGetValue(id, out var existing)
                    || attributeMutation["attributes"] is not JsonObject changedAttributes)
                {
                    continue;
                }

                var mergedAttributes = new Dictionary<string, string>(
                    existing.Attributes,
                    StringComparer.OrdinalIgnoreCase);
                AddAttributes(changedAttributes, mergedAttributes);
                nodes[id] = existing with { Attributes = mergedAttributes };
            }
        }
    }

    private static void AddPointerAction(
        ExternalReplayEvent replayEvent,
        IReadOnlyDictionary<int, ExternalReplayNode> nodes,
        ICollection<ExternalReplayAction> actions,
        ICollection<string> diagnostics)
    {
        if (!TryReadInteger(replayEvent.Data, "type", out var interactionType)
            || interactionType is not (2 or 4 or 9))
        {
            return;
        }

        var verb = interactionType == 4 ? "Double-tap" : "Tap";
        int? nodeId = TryReadInteger(replayEvent.Data, "id", out var value) ? value : null;
        ExternalReplayTarget? target = null;
        if (nodeId.HasValue
            && TryDescribeTarget(nodeId.Value, nodes, out target)
            && target is not null)
        {
            actions.Add(new ExternalReplayAction(
                interactionType == 4 ? "double-tap" : "tap",
                $"{verb} {target.Description}.",
                replayEvent.TimestampUtc,
                target.Label,
                nodeId));
            return;
        }

        var x = ReadNumber(replayEvent.Data["x"]);
        var y = ReadNumber(replayEvent.Data["y"]);
        var position = x.HasValue && y.HasValue
            ? $" near recorded replay coordinates ({x.Value:0}, {y.Value:0})"
            : " at the recorded replay position";
        actions.Add(new ExternalReplayAction(
            interactionType == 4 ? "double-tap" : "tap",
            $"{verb} the control{position}; inspect the current UI to choose the semantic target.",
            replayEvent.TimestampUtc,
            Target: null,
            nodeId));
        diagnostics.Add(
            nodeId.HasValue
                ? $"Recorded click target node {nodeId.Value} had no usable label or stable attribute."
                : "A recorded click had no target node identifier.");
    }

    private static void AddInputAction(
        ExternalReplayEvent replayEvent,
        IReadOnlyDictionary<int, ExternalReplayNode> nodes,
        ICollection<ExternalReplayAction> actions,
        ICollection<string> diagnostics)
    {
        int? nodeId = TryReadInteger(replayEvent.Data, "id", out var value) ? value : null;
        var inputText = ReadRawString(replayEvent.Data, "text") ?? string.Empty;
        ExternalReplayTarget? describedTarget = null;
        var target = nodeId.HasValue
                     && TryDescribeTarget(nodeId.Value, nodes, out describedTarget)
                     && describedTarget is not null
            ? describedTarget
            : new ExternalReplayTarget("the recorded input field", "input field");
        string instruction;
        if (TryReadBoolean(replayEvent.Data, "isChecked", out var isChecked))
        {
            var previousAction = actions.LastOrDefault();
            if (previousAction is not null
                && previousAction.NodeId == nodeId
                && previousAction.Kind is "tap" or "double-tap")
            {
                return;
            }

            instruction = $"{(isChecked ? "Check" : "Uncheck")} {target.Description}.";
        }
        else if (IsMasked(inputText))
        {
            instruction = $"Enter the required value into {target.Description}; the source replay masked the original value.";
            diagnostics.Add($"The recorded value for {target.Description} was masked and cannot be reproduced exactly.");
        }
        else if (inputText.Length == 0)
        {
            instruction = $"Clear {target.Description}.";
        }
        else
        {
            instruction = $"Enter {JsonSerializer.Serialize(inputText)} into {target.Description}.";
        }

        actions.Add(new ExternalReplayAction(
            "input",
            instruction,
            replayEvent.TimestampUtc,
            target.Label,
            nodeId));
    }

    private static void AddScrollAction(
        ExternalReplayEvent replayEvent,
        IReadOnlyDictionary<int, ExternalReplayNode> nodes,
        IDictionary<int, double> scrollOffsets,
        ICollection<ExternalReplayAction> actions)
    {
        var nodeId = TryReadInteger(replayEvent.Data, "id", out var value) ? value : 0;
        var nextY = ReadNumber(replayEvent.Data["y"]);
        if (!nextY.HasValue)
        {
            return;
        }

        var previousY = scrollOffsets.TryGetValue(nodeId, out var previousValue)
            ? previousValue
            : 0;
        scrollOffsets[nodeId] = nextY.Value;
        if (Math.Abs(nextY.Value - previousY) < 8)
        {
            return;
        }

        var direction = nextY.Value > previousY ? "down" : "up";
        var hasTarget = TryDescribeTarget(nodeId, nodes, out var describedTarget)
                        && describedTarget is not null;
        var target = hasTarget
            ? $" within {describedTarget!.Description}"
            : string.Empty;
        actions.Add(new ExternalReplayAction(
            "scroll",
            $"Scroll {direction}{target}.",
            replayEvent.TimestampUtc,
            describedTarget?.Label,
            nodeId));
    }

    private static void ProcessCustomEvent(
        ExternalReplayEvent replayEvent,
        ICollection<ExternalReplayAction> actions)
    {
        var tag = ReadString(replayEvent.Data, "tag");
        if (!string.Equals(tag, "breadcrumb", StringComparison.OrdinalIgnoreCase)
            || replayEvent.Data["payload"] is not JsonObject payload)
        {
            return;
        }

        var category = ReadString(payload, "category");
        var message = NormalizeLabel(ReadString(payload, "message"));
        if (category?.Contains("ui.click", StringComparison.OrdinalIgnoreCase) == true
            && message is not null)
        {
            actions.Add(new ExternalReplayAction(
                "tap",
                $"Tap the control described by the source replay as {JsonSerializer.Serialize(message)}.",
                replayEvent.TimestampUtc,
                message,
                NodeId: null));
        }
    }

    private static void AddPostHogAutocaptureFallback(
        JsonNode root,
        ICollection<ExternalReplayAction> actions)
    {
        var events = new List<JsonObject>();
        CollectNamedEvents(root, events);
        foreach (var value in events)
        {
            var eventName = ReadString(value, "event");
            if (value["properties"] is not JsonObject properties)
            {
                continue;
            }

            var timestamp = TryReadTimestamp(value["timestamp"], out var capturedAtUtc)
                ? capturedAtUtc
                : (DateTimeOffset?)null;
            if (string.Equals(eventName, "$screen", StringComparison.OrdinalIgnoreCase))
            {
                var screenName = NormalizeLabel(
                    ReadString(properties, "$screen_name")
                    ?? ReadString(properties, "$screenName"));
                if (screenName is not null)
                {
                    actions.Add(new ExternalReplayAction(
                        "navigate",
                        $"Navigate to the {JsonSerializer.Serialize(screenName)} screen.",
                        timestamp,
                        screenName,
                        NodeId: null));
                }
            }
            else if (string.Equals(eventName, "$autocapture", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(
                         ReadString(properties, "$event_type"),
                         "click",
                         StringComparison.OrdinalIgnoreCase))
            {
                var label = NormalizeLabel(
                    ReadString(properties, "$el_text")
                    ?? ReadString(properties, "$element_text"));
                if (label is not null)
                {
                    actions.Add(new ExternalReplayAction(
                        "tap",
                        $"Tap the visible control with text {JsonSerializer.Serialize(label)}.",
                        timestamp,
                        label,
                        NodeId: null));
                }
            }
        }
    }

    private static void CollectNamedEvents(JsonNode? node, ICollection<JsonObject> events)
    {
        if (node is JsonObject value)
        {
            if (value["event"] is JsonValue && value["properties"] is JsonObject)
            {
                events.Add(value);
            }

            foreach (var property in value)
            {
                CollectNamedEvents(property.Value, events);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                CollectNamedEvents(item, events);
            }
        }
    }

    private static List<ExternalReplayAction> CompactActions(
        IReadOnlyList<ExternalReplayAction> actions)
    {
        var result = new List<ExternalReplayAction>();
        foreach (var action in actions.OrderBy(static action => action.CapturedAtUtc))
        {
            if (result.Count > 0
                && action.Kind is "input" or "scroll"
                && string.Equals(result[^1].Kind, action.Kind, StringComparison.Ordinal)
                && result[^1].NodeId == action.NodeId)
            {
                result[^1] = action;
                continue;
            }

            result.Add(action);
        }

        return result;
    }

    private static void AddNodeTree(
        JsonObject value,
        IDictionary<int, ExternalReplayNode> nodes,
        int? parentId)
    {
        var hasId = TryReadInteger(value, "id", out var id);
        var currentParentId = hasId ? id : parentId;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value["attributes"] is JsonObject sourceAttributes)
        {
            AddAttributes(sourceAttributes, attributes);
        }

        var text = NormalizeLabel(ReadString(value, "textContent"));
        var childTexts = new List<string>();
        if (value["childNodes"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                AddNodeTree(child, nodes, currentParentId);
                var childText = NormalizeLabel(ReadString(child, "textContent"));
                if (childText is not null)
                {
                    childTexts.Add(childText);
                }
            }
        }

        text ??= NormalizeLabel(string.Join(' ', childTexts));
        if (hasId)
        {
            nodes[id] = new ExternalReplayNode(
                id,
                NormalizeOptional(ReadString(value, "tagName"))?.ToLowerInvariant(),
                text,
                attributes,
                parentId);
        }
    }

    private static void AddAttributes(
        JsonObject source,
        IDictionary<string, string> target)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonValue value
                && value.TryGetValue<string>(out var text))
            {
                target[property.Key] = text;
            }
        }
    }

    private static bool TryDescribeTarget(
        int nodeId,
        IReadOnlyDictionary<int, ExternalReplayNode> nodes,
        out ExternalReplayTarget? target)
    {
        target = null;
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        var label = ReadAttribute(node, "aria-label")
                    ?? node.Text
                    ?? ReadAttribute(node, "placeholder")
                    ?? ReadAttribute(node, "title")
                    ?? ReadAttribute(node, "alt")
                    ?? ReadAttribute(node, "data-testid")
                    ?? ReadAttribute(node, "data-test")
                    ?? ReadAttribute(node, "name")
                    ?? ReadAttribute(node, "id");
        label = NormalizeLabel(label);
        if (label is null && node.ParentId.HasValue)
        {
            return TryDescribeTarget(node.ParentId.Value, nodes, out target);
        }

        if (label is null)
        {
            return false;
        }

        var role = ReadAttribute(node, "role")
                   ?? (node.TagName == "input" ? ReadAttribute(node, "type") : null)
                   ?? node.TagName
                   ?? "control";
        var description = role.ToLowerInvariant() switch
        {
            "input" or "textarea" or "textbox" => $"the {JsonSerializer.Serialize(label)} input field",
            "checkbox" => $"the {JsonSerializer.Serialize(label)} checkbox",
            "radio" => $"the {JsonSerializer.Serialize(label)} radio option",
            "button" => $"the {JsonSerializer.Serialize(label)} button",
            "a" or "link" => $"the {JsonSerializer.Serialize(label)} link",
            _ => $"the visible {role} labelled {JsonSerializer.Serialize(label)}"
        };
        target = new ExternalReplayTarget(description, label);
        return true;
    }

    private static string? ReadAttribute(ExternalReplayNode node, string name)
        => node.Attributes.TryGetValue(name, out var value) ? NormalizeLabel(value) : null;

    private static string? ReadRawString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string NormalizeProvider(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "sentry" => "sentry",
            "posthog" or "post-hog" => "posthog",
            _ => throw new CliUsageException(
                $"Unsupported replay provider '{provider}'. Expected sentry or posthog.")
        };

    private static string? NormalizeRoute(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        }

        return normalized;
    }

    private static bool IsMasked(string value)
    {
        var containsMaskCharacter = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            if (character is not ('*' or '•' or '●' or '◦' or '·'))
            {
                return false;
            }

            containsMaskCharacter = true;
        }

        return containsMaskCharacter;
    }

    private static bool TryReadTimestamp(JsonNode? value, out DateTimeOffset timestampUtc)
    {
        timestampUtc = default;
        var number = ReadNumber(value);
        if (number.HasValue && double.IsFinite(number.Value))
        {
            try
            {
                timestampUtc = number.Value > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)number.Value)
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)(number.Value * 1000));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return value is JsonValue jsonValue
               && jsonValue.TryGetValue<string>(out var text)
               && DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out timestampUtc);
    }

    private static double? ReadNumber(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        if (jsonValue.TryGetValue<double>(out var number))
        {
            return number;
        }

        return jsonValue.TryGetValue<string>(out var text)
               && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool TryReadInteger(JsonObject value, string name, out int result)
    {
        result = default;
        return value[name] is JsonValue jsonValue
               && (jsonValue.TryGetValue<int>(out result)
                   || jsonValue.TryGetValue<long>(out var longValue)
                   && longValue is >= int.MinValue and <= int.MaxValue
                   && (result = (int)longValue) == longValue);
    }

    private static string? ReadString(JsonObject value, string name)
        => value[name] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool TryReadBoolean(JsonObject value, string name, out bool result)
    {
        result = default;
        return value[name] is JsonValue jsonValue
               && (jsonValue.TryGetValue<bool>(out result)
                   || jsonValue.TryGetValue<string>(out var text)
                   && bool.TryParse(text, out result));
    }

    private static string? NormalizeLabel(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        normalized = string.Join(' ', normalized.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= MaximumLabelCharacters
            ? normalized
            : normalized[..MaximumLabelCharacters] + "…";
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ExternalReplayEvent(
        int Type,
        DateTimeOffset TimestampUtc,
        JsonObject Data);

    private sealed record ExternalReplayNode(
        int Id,
        string? TagName,
        string? Text,
        IReadOnlyDictionary<string, string> Attributes,
        int? ParentId);

    private sealed record ExternalReplayTarget(string Description, string Label);

    private sealed record ExternalReplayAction(
        string Kind,
        string Instruction,
        DateTimeOffset? CapturedAtUtc,
        string? Target,
        int? NodeId);

    private sealed class ExternalReplayEventComparer : IComparer<ExternalReplayEvent>
    {
        public static ExternalReplayEventComparer Instance { get; } = new();

        public int Compare(ExternalReplayEvent? left, ExternalReplayEvent? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            return right is null ? 1 : left.TimestampUtc.CompareTo(right.TimestampUtc);
        }
    }
}
