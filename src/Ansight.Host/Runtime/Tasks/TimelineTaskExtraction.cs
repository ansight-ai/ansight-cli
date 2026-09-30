using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>
/// A reviewable repository task draft extracted from a historical session range.
/// </summary>
public sealed record TimelineTaskExtraction(
    string SuggestedName,
    string Source,
    int GestureCount,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics)
{
    internal IReadOnlyList<TimelineExtractedAction> Actions { get; init; } = [];

    public IReadOnlyList<ReplayStep> ReplaySteps { get; init; } = [];

    public IReadOnlyList<string> ReplayInstructions => ReplaySteps
        .Select(static step => step.Instruction)
        .ToArray();

    public string Summary => Diagnostics.Count == 0
        ? $"Generated {GeneratedActionCount:N0} replay action(s) from captured interaction evidence."
        : $"Generated {GeneratedActionCount:N0} replay action(s) from captured interaction evidence · "
          + $"{Diagnostics.Count:N0} review item(s).";
}

/// <summary>
/// Converts recorded touch and visual-tree evidence into a deterministic Ansight repository task draft.
/// </summary>
public static class TimelineTaskExtractor
{
    private static readonly TimeSpan gestureGap = TimeSpan.FromMilliseconds(
        TouchReviewDefaults.DefaultGestureGapMilliseconds);
    private static readonly TimeSpan targetWindow = TimeSpan.FromMilliseconds(
        TouchReviewDefaults.DefaultTapTargetWindowMilliseconds);
    private static readonly TimeSpan inputBurstGap = TimeSpan.FromSeconds(3);

    public static TimelineTaskExtraction Extract(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        bool includeCoordinateFallbackTaps = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        startUtc = startUtc.ToUniversalTime();
        endUtc = endUtc.ToUniversalTime();
        if (endUtc < startUtc)
        {
            (startUtc, endUtc) = (endUtc, startUtc);
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentException("The task extraction range must have a positive duration.");
        }

        var touches = snapshot.Touches
            .Where(touch => touch.CapturedAtUtc >= startUtc && touch.CapturedAtUtc <= endUtc)
            .OrderBy(touch => touch.CapturedAtUtc)
            .ThenBy(touch => touch.Id, StringComparer.Ordinal)
            .ToArray();
        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, gestureGap);
        var diagnostics = new List<string>();
        var actions = new List<GeneratedTaskAction>();
        foreach (var gesture in gestures)
        {
            if (TouchReviewGeometry.NormalizeTouchAction(gesture.Touches[^1].Action) == "cancel")
            {
                diagnostics.Add(
                    $"Gesture {gesture.GestureId} was cancelled and was not generated; "
                    + "replaying it as a completed tap or swipe could perform a different action.");
                continue;
            }

            switch (gesture.Kind)
            {
                case "tap":
                    AddTapAction(
                        snapshot,
                        gesture,
                        actions,
                        diagnostics,
                        includeCoordinateFallbackTaps);
                    break;
                case "drag":
                    AddSwipeAction(gesture, actions, diagnostics);
                    break;
                case "longPress":
                    diagnostics.Add(
                        $"Gesture {gesture.GestureId} is a long press; repository tasks do not yet expose a long-press action.");
                    break;
                case "multiTouch":
                    diagnostics.Add(
                        $"Gesture {gesture.GestureId} uses multiple pointers and must be recreated manually.");
                    break;
                default:
                    diagnostics.Add(
                        $"Gesture {gesture.GestureId} is incomplete or unclassified and was not generated.");
                    break;
            }
        }

        AddVisualTreeInputActions(
            snapshot,
            startUtc,
            endUtc,
            actions,
            diagnostics);
        actions = CompactAndOrderActions(actions);

        if (touches.Length == 0)
        {
            diagnostics.Add("The selected timeline range contains no recorded touch input.");
        }

        while (CalculateToolCallCount(actions) + 1 > 100)
        {
            var removed = actions[^1];
            actions.RemoveAt(actions.Count - 1);
            diagnostics.Add(
                $"The selection exceeds the 100-call task limit; omitted trailing action {removed.Description}.");
        }

        var normalizedTitle = title.Trim();
        var source = BuildSource(
            snapshot,
            startUtc,
            endUtc,
            normalizedTitle,
            actions
                .Where(static action => action.Kind != "tap" || action.Selector is not null)
                .ToArray(),
            diagnostics);
        return new TimelineTaskExtraction(
            Slugify(normalizedTitle),
            source,
            gestures.Count,
            actions.Count,
            diagnostics)
        {
            Actions = actions.Select(static action => new TimelineExtractedAction(
                action.Kind,
                action.Selector?.DeepClone().AsObject(),
                action.DurationMilliseconds,
                action.Description,
                action.CapturedAtUtc,
                action.InputValue,
                action.StartNormalizedX,
                action.StartNormalizedY,
                action.EndNormalizedX,
                action.EndNormalizedY)).ToArray(),
            ReplaySteps = actions
                .Select((action, index) => BuildReplayStep(snapshot, action, index + 1))
                .ToArray()
        };
    }

    private static void AddTapAction(
        AppSessionSnapshot snapshot,
        TouchGestureSegment gesture,
        ICollection<GeneratedTaskAction> actions,
        ICollection<string> diagnostics,
        bool includeCoordinateFallback)
    {
        var point = TouchReviewGeometry.ResolveGesturePoint(gesture);
        if (!point.HasValue)
        {
            diagnostics.Add($"Tap {gesture.GestureId} has no usable normalized position.");
            return;
        }

        var candidates = TapTargetMatcher.FindTapTargetCandidates(
                snapshot,
                gesture,
                point.Value,
                targetWindow)
            .Select(TryCreateSelectorCandidate)
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .GroupBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        if (candidates.Length == 0)
        {
            if (!includeCoordinateFallback)
            {
                diagnostics.Add(
                    $"Tap {gesture.GestureId} at ({point.Value.X:0.###}, {point.Value.Y:0.###}) has no stable UI selector.");
                return;
            }

            actions.Add(new GeneratedTaskAction(
                "tap",
                Selector: null,
                gesture.DurationMilliseconds,
                Length: null,
                Orientation: null,
                "recorded viewport point",
                gesture.StartUtc,
                gesture.StartUtc,
                TargetKey: null,
                InputValue: null,
                VisualTreeSnapshotId: null,
                point.Value.X,
                point.Value.Y,
                EndNormalizedX: null,
                EndNormalizedY: null));
            diagnostics.Add(
                $"Tap {gesture.GestureId} at ({point.Value.X:0.###}, {point.Value.Y:0.###}) has no stable UI selector; "
                + "replay will use its recorded normalized viewport point.");
            return;
        }

        var selected = candidates[0];
        actions.Add(new GeneratedTaskAction(
            "tap",
            selected.Selector,
            gesture.DurationMilliseconds,
            Length: null,
            Orientation: null,
            selected.Description,
            gesture.StartUtc,
            gesture.StartUtc,
            selected.Key,
            InputValue: null,
            selected.VisualTreeSnapshotId,
            point.Value.X,
            point.Value.Y,
            EndNormalizedX: null,
            EndNormalizedY: null));
        if (candidates.Length > 1 && candidates[1].Score == selected.Score)
        {
            diagnostics.Add(
                $"Tap {gesture.GestureId} matched multiple equally ranked targets; review selector {selected.Description}.");
        }
    }

    private static SelectorCandidate? TryCreateSelectorCandidate(JsonObject candidate)
    {
        var source = ReadString(candidate, "source");
        var target = string.Equals(source, "annotationTarget", StringComparison.Ordinal)
            ? candidate["target"] as JsonObject
            : candidate;
        if (target is null)
        {
            return null;
        }

        var automationId = ReadString(target, "automationId");
        var label = ReadString(target, "label");
        var type = ReadString(target, "type");
        var depth = ReadInteger(target, "depth");
        var specificityScore = ReadBoundsSpecificityScore(candidate);
        var visualTreeSnapshotId = ReadString(target, "visualTreeSnapshotId")
                                   ?? ReadString(candidate, "visualTreeSnapshotId");
        if (automationId is not null)
        {
            var selector = new JsonObject
            {
                ["automationId"] = automationId,
                ["visible"] = true,
                ["enabled"] = true
            };
            return new SelectorCandidate(
                selector,
                10_000 + specificityScore + depth,
                $"automationId={automationId}",
                BuildTargetKey(selector),
                visualTreeSnapshotId);
        }

        if (label is null)
        {
            return null;
        }

        var textSelector = new JsonObject
        {
            ["text"] = label,
            ["visible"] = true,
            ["enabled"] = true
        };
        if (type is not null)
        {
            textSelector["type"] = type;
        }

        return new SelectorCandidate(
            textSelector,
            5_000 + (type is null ? 0 : 500) + specificityScore + depth,
            type is null ? $"text={label}" : $"text={label}, type={type}",
            BuildTargetKey(textSelector),
            visualTreeSnapshotId);
    }

    private static int ReadBoundsSpecificityScore(JsonObject candidate)
    {
        if (candidate["bounds"] is not JsonObject bounds
            || bounds["width"] is not JsonValue widthValue
            || bounds["height"] is not JsonValue heightValue
            || !widthValue.TryGetValue<double>(out var width)
            || !heightValue.TryGetValue<double>(out var height)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            return 0;
        }

        return (int)Math.Round((1 - Math.Clamp(width * height, 0, 1)) * 1_000);
    }

    private static void AddSwipeAction(
        TouchGestureSegment gesture,
        ICollection<GeneratedTaskAction> actions,
        ICollection<string> diagnostics)
    {
        var startPoint = TouchReviewGeometry.ResolveTouchPoint(gesture.Touches[0]);
        var endPoint = TouchReviewGeometry.ResolveTouchPoint(gesture.Touches[^1]);
        if (!startPoint.HasValue || !endPoint.HasValue)
        {
            diagnostics.Add($"Drag {gesture.GestureId} has no usable normalized path.");
            return;
        }

        var dx = endPoint.Value.X - startPoint.Value.X;
        var dy = endPoint.Value.Y - startPoint.Value.Y;
        var orientation = ResolveOrientation(dx, dy);
        var length = Math.Clamp(
            gesture.DistanceNormalized ?? TouchReviewGeometry.CalculateDistance(startPoint.Value, endPoint.Value),
            0.05,
            0.9);
        actions.Add(new GeneratedTaskAction(
            "swipe",
            Selector: null,
            Math.Clamp(gesture.DurationMilliseconds, 50, 2_000),
            length,
            orientation,
            $"{orientation} swipe",
            gesture.StartUtc,
            gesture.StartUtc,
            TargetKey: null,
            InputValue: null,
            VisualTreeSnapshotId: null,
            startPoint.Value.X,
            startPoint.Value.Y,
            endPoint.Value.X,
            endPoint.Value.Y));
    }

    private static void AddVisualTreeInputActions(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        ICollection<GeneratedTaskAction> actions,
        ICollection<string> diagnostics)
    {
        var trees = snapshot.VisualTreeSnapshots
            .Where(tree => tree.CapturedAtUtc <= endUtc)
            .OrderBy(tree => tree.CapturedAtUtc)
            .ThenBy(tree => tree.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        if (trees.Length == 0)
        {
            return;
        }

        var generatedInputCount = 0;
        var omittedValueCount = 0;
        var observedReadableInput = false;
        var comparedReadableInput = false;
        var weakSelectorWarnings = new HashSet<string>(StringComparer.Ordinal);
        var maskedValueWarnings = new HashSet<string>(StringComparer.Ordinal);
        var uncorrelatedInputWarnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in trees.GroupBy(BuildVisualTreeStreamKey, StringComparer.Ordinal))
        {
            var inRange = stream
                .Where(tree => tree.CapturedAtUtc >= startUtc)
                .ToList();
            if (inRange.Count == 0)
            {
                continue;
            }

            var baseline = stream.LastOrDefault(tree => tree.CapturedAtUtc < startUtc);
            if (baseline is not null)
            {
                inRange.Insert(0, baseline);
            }

            InputTreeReadResult? previous = null;
            SessionVisualTreeSnapshot? previousTree = null;
            foreach (var tree in inRange)
            {
                var current = ReadInputTree(tree);
                omittedValueCount += current.OmittedValueCount;
                observedReadableInput |= current.States.Count > 0;
                if (previous is not null && tree.CapturedAtUtc >= startUtc)
                {
                    foreach (var state in current.States.Values)
                    {
                        if (!previous.States.TryGetValue(state.Identity, out var earlier))
                        {
                            continue;
                        }

                        comparedReadableInput = true;
                        if (string.Equals(earlier.Value, state.Value, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (IsMaskedInputValue(state.Value))
                        {
                            if (maskedValueWarnings.Add(state.Description))
                            {
                                diagnostics.Add(
                                    $"The captured value for {state.Description} was masked and cannot be replayed exactly. "
                                    + "Use a host-managed secret or explicit task input for this field.");
                            }

                            continue;
                        }

                        if (!state.HasStableSelector && weakSelectorWarnings.Add(state.Description))
                        {
                            diagnostics.Add(
                                $"Text entry for {state.Description} has no automation ID or stable label; "
                                + "replay will use its role and relative index.");
                        }

                        var targetKey = BuildTargetKey(state.Selector);
                        if (!HasCorrelatedFocusTap(
                                actions,
                                previousTree!.CapturedAtUtc,
                                tree.CapturedAtUtc,
                                targetKey)
                            && uncorrelatedInputWarnings.Add(state.Description))
                        {
                            diagnostics.Add(
                                $"The value change for {state.Description} had no correlated focus tap. "
                                + "It may represent typing, paste, autofill, or a programmatic update; review this replay step.");
                        }

                        actions.Add(new GeneratedTaskAction(
                            "input",
                            state.Selector,
                            DurationMilliseconds: 0,
                            Length: null,
                            Orientation: null,
                            state.Value.Length == 0
                                ? $"clear {state.Description}"
                                : $"replace text in {state.Description}",
                            tree.CapturedAtUtc,
                            ResolveInputOrderTime(
                                actions,
                                previousTree!.CapturedAtUtc,
                                tree.CapturedAtUtc,
                                targetKey),
                            targetKey,
                            state.Value,
                            tree.SnapshotId,
                            StartNormalizedX: null,
                            StartNormalizedY: null,
                            EndNormalizedX: null,
                            EndNormalizedY: null));
                        generatedInputCount++;
                    }
                }

                previous = current;
                previousTree = tree;
            }
        }

        if (omittedValueCount > 0)
        {
            diagnostics.Add(
                "Captured visual trees contained input controls without readable values. "
                + "Secure values and values omitted by a provider cannot be reconstructed from visual-tree evidence.");
        }

        if (generatedInputCount == 0
            && observedReadableInput
            && !comparedReadableInput
            && maskedValueWarnings.Count == 0)
        {
            diagnostics.Add(
                "Captured input controls had no comparable consecutive visual-tree snapshots. "
                + "Text entry cannot be inferred from a single state; capture a tree before and after editing.");
        }
    }

    private static InputTreeReadResult ReadInputTree(SessionVisualTreeSnapshot tree)
    {
        if (tree.Payload["root"] is not JsonObject root)
        {
            return InputTreeReadResult.Empty;
        }

        var typeRegistry = VisualTreeTypeRegistry.FromPayload(tree.Payload);
        var states = new Dictionary<string, ReplayInputNodeState>(StringComparer.Ordinal);
        var occurrenceByBaseIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        var omittedValueCount = 0;
        foreach (var match in LiveUiNodeQuery.Enumerate(root, typeRegistry))
        {
            var role = LiveUiNodeQuery.ReadRole(match.Node, typeRegistry);
            var supportedActions = LiveUiNodeQuery.ReadSupportedActions(match.Node, typeRegistry);
            if (!IsTextInput(role, supportedActions))
            {
                continue;
            }

            if (!TryReadInputValue(match.Node, out var value))
            {
                omittedValueCount++;
                continue;
            }

            var automationId = LiveUiNodeQuery.ReadAutomationId(match.Node);
            var stableLabel = ReadStableInputLabel(match.Node, value);
            var ancestorAutomationId = match.Ancestors
                .Select(LiveUiNodeQuery.ReadAutomationId)
                .LastOrDefault(static candidate => candidate is not null);
            var type = typeRegistry.Resolve(match.Node);
            var baseIdentity = automationId is not null
                ? $"automation:{automationId}"
                : stableLabel is not null
                    ? $"label:{role}:{stableLabel}"
                    : ancestorAutomationId is not null
                        ? $"ancestor:{ancestorAutomationId}:{role}:{type}"
                        : $"role:{role}:{type}";
            var occurrence = occurrenceByBaseIdentity.GetValueOrDefault(baseIdentity);
            occurrenceByBaseIdentity[baseIdentity] = occurrence + 1;
            var identity = $"{baseIdentity}:{occurrence}";
            var selector = BuildInputSelector(
                automationId,
                stableLabel,
                ancestorAutomationId,
                role,
                type,
                occurrence);
            var description = automationId is not null
                ? $"the input field with automation ID {SerializeString(automationId)}"
                : stableLabel is not null
                    ? $"the {SerializeString(stableLabel)} input field"
                    : ancestorAutomationId is not null
                        ? $"input field {occurrence + 1} within {SerializeString(ancestorAutomationId)}"
                        : $"visible {role} {occurrence + 1}";
            states[identity] = new ReplayInputNodeState(
                identity,
                selector,
                description,
                value,
                automationId is not null || stableLabel is not null);
        }

        return new InputTreeReadResult(states, omittedValueCount);
    }

    private static JsonObject BuildInputSelector(
        string? automationId,
        string? label,
        string? ancestorAutomationId,
        string role,
        string? type,
        int occurrence)
    {
        var selector = new JsonObject
        {
            ["visible"] = true,
            ["enabled"] = true
        };
        if (automationId is not null)
        {
            selector["automationId"] = automationId;
            return selector;
        }

        if (label is not null)
        {
            selector["text"] = label;
            selector["role"] = role;
            selector["index"] = occurrence;
            return selector;
        }

        selector["role"] = role;
        if (ancestorAutomationId is not null)
        {
            selector["ancestorAutomationId"] = ancestorAutomationId;
        }
        else if (type is not null)
        {
            selector["type"] = type;
        }

        selector["index"] = occurrence;
        return selector;
    }

    private static bool IsTextInput(string role, IReadOnlyList<string> supportedActions)
        => role is "input" or "textbox" or "searchbox"
           || supportedActions.Any(action => action.Equals("typeText", StringComparison.OrdinalIgnoreCase)
                                             || action.Equals("setValue", StringComparison.OrdinalIgnoreCase));

    private static bool TryReadInputValue(JsonObject node, out string value)
    {
        foreach (var candidate in new[]
                 {
                     node["value"],
                     (node["visual"] as JsonObject)?["value"],
                     (node["properties"] as JsonObject)?["value"],
                     (node["props"] as JsonObject)?["value"]
                 })
        {
            if (candidate is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out var text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string? ReadStableInputLabel(JsonObject node, string value)
    {
        var normalizedValue = NormalizeOptional(value);
        var labels = new[]
        {
            ReadRawString(node, "label"),
            ReadNestedRawString(node, "visual", "text"),
            ReadNestedRawString(node, "props", "accessibilityLabel"),
            ReadNestedRawString(node, "props", "title"),
            ReadNestedRawString(node, "properties", "placeholder"),
            ReadNestedRawString(node, "properties", "hint")
        };
        return labels
            .Select(NormalizeOptional)
            .FirstOrDefault(label => label is not null
                                     && !string.Equals(label, normalizedValue, StringComparison.Ordinal));
    }

    private static string? ReadRawString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string? ReadNestedRawString(
        JsonObject value,
        string objectName,
        string propertyName)
        => value[objectName] is JsonObject nested
            ? ReadRawString(nested, propertyName)
            : null;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildTargetKey(JsonObject selector)
    {
        var names = new[]
        {
            "automationId",
            "text",
            "role",
            "type",
            "ancestorAutomationId",
            "action",
            "index"
        };
        return string.Join(
            '\u001f',
            names.Select(name => selector[name]?.ToJsonString() ?? string.Empty));
    }

    private static bool IsMaskedInputValue(string value)
    {
        var containsMaskCharacter = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            if (character is not ('*' or '\u2022' or '\u25CF' or '\u25E6' or '\u00B7'))
            {
                return false;
            }

            containsMaskCharacter = true;
        }

        return containsMaskCharacter;
    }

    private static string BuildVisualTreeStreamKey(SessionVisualTreeSnapshot tree)
        => $"{tree.Source}\u001f{tree.VisualTreeKind}\u001f{tree.VisualTreeFormat}\u001f{tree.RootScope}";

    private static List<GeneratedTaskAction> CompactAndOrderActions(
        IEnumerable<GeneratedTaskAction> source)
    {
        var result = new List<GeneratedTaskAction>();
        foreach (var action in source
                     .OrderBy(static action => action.OrderAtUtc)
                     .ThenBy(static action => action.Kind == "input" ? 0 : 1))
        {
            if (action.Kind == "input" && action.TargetKey is not null && result.Count > 0)
            {
                var previous = result[^1];
                if (previous.Kind == "tap"
                    && string.Equals(previous.TargetKey, action.TargetKey, StringComparison.Ordinal))
                {
                    result.RemoveAt(result.Count - 1);
                }
                else if (previous.Kind == "input"
                         && string.Equals(previous.TargetKey, action.TargetKey, StringComparison.Ordinal)
                         && action.CapturedAtUtc - previous.CapturedAtUtc <= inputBurstGap)
                {
                    result[^1] = action;
                    continue;
                }
            }

            result.Add(action);
        }

        return result;
    }

    private static DateTimeOffset ResolveInputOrderTime(
        IEnumerable<GeneratedTaskAction> actions,
        DateTimeOffset previousTreeCapturedAtUtc,
        DateTimeOffset currentTreeCapturedAtUtc,
        string inputTargetKey)
    {
        var nextDifferentTap = actions
            .Where(action => action.Kind == "tap"
                             && action.CapturedAtUtc > previousTreeCapturedAtUtc
                             && action.CapturedAtUtc <= currentTreeCapturedAtUtc
                             && action.TargetKey is not null
                             && !string.Equals(action.TargetKey, inputTargetKey, StringComparison.Ordinal))
            .OrderBy(static action => action.CapturedAtUtc)
            .FirstOrDefault();
        return nextDifferentTap is null
            ? currentTreeCapturedAtUtc
            : nextDifferentTap.CapturedAtUtc.AddTicks(-1);
    }

    private static bool HasCorrelatedFocusTap(
        IEnumerable<GeneratedTaskAction> actions,
        DateTimeOffset previousTreeCapturedAtUtc,
        DateTimeOffset currentTreeCapturedAtUtc,
        string inputTargetKey)
        => actions.Any(action => action.Kind == "tap"
                                 && action.CapturedAtUtc >= previousTreeCapturedAtUtc
                                 && action.CapturedAtUtc <= currentTreeCapturedAtUtc
                                 && string.Equals(action.TargetKey, inputTargetKey, StringComparison.Ordinal));

    private static string BuildSource(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        IReadOnlyList<GeneratedTaskAction> actions,
        IReadOnlyList<string> diagnostics)
    {
        var diagnosticComments = string.Concat(diagnostics.Select(diagnostic =>
            $" * REVIEW: {SanitizeComment(diagnostic)}\n"));
        var body = actions.Count == 0
            ? EmbeddedTextResource.Read(
                "Runtime/Tasks/Resources/timeline-task-empty-body.ts").TrimEnd()
            : BuildActionBody(actions);
        return EmbeddedTextResource.Render(
            "Runtime/Tasks/Resources/timeline-task-template.ts",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SESSION_ID_TEXT"] = snapshot.SessionId,
                ["START_UTC"] = startUtc.ToString("O"),
                ["END_UTC"] = endUtc.ToString("O"),
                ["DIAGNOSTIC_COMMENTS"] = diagnosticComments,
                ["APP_ID"] = SerializeString(snapshot.AppId),
                ["TITLE"] = SerializeString(title),
                ["DESCRIPTION"] = SerializeString(EmbeddedTextResource.RenderSection(
                    "Runtime/Tasks/Resources/replay-prompts.md",
                    "task-description",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["START_UTC"] = startUtc.ToString("O"),
                        ["END_UTC"] = endUtc.ToString("O")
                    }).TrimEnd()),
                ["MAXIMUM_ACTIONS"] = Math.Clamp(
                    CalculateToolCallCount(actions) + 1,
                    1,
                    100).ToString(CultureInfo.InvariantCulture),
                ["BODY"] = body,
                ["ACTION_COUNT"] = actions.Count.ToString(CultureInfo.InvariantCulture),
                ["SESSION_ID"] = SerializeString(snapshot.SessionId)
            }).TrimEnd();
    }

    private static string BuildActionBody(IReadOnlyList<GeneratedTaskAction> actions)
    {
        var body = new StringBuilder();
        for (var index = 0; index < actions.Count; index++)
        {
            body.Append(BuildAction(actions[index], index + 1));
        }

        body.Append(EmbeddedTextResource.Read(
            "Runtime/Tasks/Resources/timeline-task-final-body.ts").TrimEnd());
        return body.ToString();
    }

    private static string BuildAction(GeneratedTaskAction action, int sequence)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SEQUENCE"] = sequence.ToString(CultureInfo.InvariantCulture),
            ["DESCRIPTION"] = SanitizeComment(action.Description),
            ["PERFORMED_MESSAGE"] = SerializeString(action.Description + " should be performed.")
        };
        if (action.Kind is "tap" or "input")
        {
            var operationPath = "Runtime/Tasks/Resources/timeline-task-tap-operation.ts";
            var operationValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SEQUENCE"] = sequence.ToString(CultureInfo.InvariantCulture),
                ["SELECTOR"] = action.Selector!.ToJsonString()
            };
            if (action.Kind == "input")
            {
                var inputArguments = action.Selector!.DeepClone().AsObject();
                inputArguments["value"] = action.InputValue;
                inputArguments["replaceExisting"] = true;
                operationPath = "Runtime/Tasks/Resources/timeline-task-input-operation.ts";
                operationValues.Clear();
                operationValues["SEQUENCE"] = sequence.ToString(CultureInfo.InvariantCulture);
                operationValues["ARGUMENTS"] = inputArguments.ToJsonString();
            }

            values["SELECTOR_PROPERTIES"] = BuildJsonProperties(action.Selector!);
            values["OPERATION"] = EmbeddedTextResource.Render(
                operationPath,
                operationValues).TrimEnd();
            return EmbeddedTextResource.Render(
                "Runtime/Tasks/Resources/timeline-task-target-action.ts",
                values).TrimEnd();
        }

        values["ORIENTATION"] = SerializeString(action.Orientation!);
        values["LENGTH"] = action.Length!.Value.ToString("0.###", CultureInfo.InvariantCulture);
        values["DURATION_MILLISECONDS"] = action.DurationMilliseconds.ToString(CultureInfo.InvariantCulture);
        return EmbeddedTextResource.Render(
            "Runtime/Tasks/Resources/timeline-task-swipe-action.ts",
            values).TrimEnd();
    }

    private static string BuildJsonProperties(JsonObject value)
        => string.Concat(value.Select(property =>
            $"    {property.Key}: {property.Value?.ToJsonString() ?? "null"},\n"));

    private static int CalculateToolCallCount(IEnumerable<GeneratedTaskAction> actions)
        => actions.Sum(static action => action.Kind is "tap" or "input" ? 2 : 1);

    private static ReplayStep BuildReplayStep(
        AppSessionSnapshot snapshot,
        GeneratedTaskAction action,
        int sequence)
    {
        var visualTree = action.VisualTreeSnapshotId is null
            ? null
            : snapshot.VisualTreeSnapshots.FirstOrDefault(tree => string.Equals(
                tree.SnapshotId,
                action.VisualTreeSnapshotId,
                StringComparison.Ordinal));
        var screenshotFrameId = visualTree?.ScreenshotFrameId
                                ?? snapshot.Images
                                    .Where(image => Math.Abs(
                                        (image.CapturedAtUtc - action.CapturedAtUtc).TotalMilliseconds)
                                                    <= targetWindow.TotalMilliseconds)
                                    .OrderBy(image => Math.Abs(
                                        (image.CapturedAtUtc - action.CapturedAtUtc).TotalMilliseconds))
                                    .Select(static image => image.FrameId)
                                    .FirstOrDefault();
        var hasSourceEvidence = action.VisualTreeSnapshotId is not null
                                || screenshotFrameId is not null
                                || action.StartNormalizedX.HasValue
                                || action.StartNormalizedY.HasValue
                                || action.EndNormalizedX.HasValue
                                || action.EndNormalizedY.HasValue;
        return new ReplayStep(
            sequence,
            action.Kind,
            BuildReplayInstruction(action),
            action.CapturedAtUtc,
            action.Description)
        {
            SourceEvidence = hasSourceEvidence
                ? new ReplaySourceEvidence
                {
                    VisualTreeSnapshotId = action.VisualTreeSnapshotId,
                    ScreenshotFrameId = screenshotFrameId,
                    StartNormalizedX = action.StartNormalizedX,
                    StartNormalizedY = action.StartNormalizedY,
                    EndNormalizedX = action.EndNormalizedX,
                    EndNormalizedY = action.EndNormalizedY,
                    DurationMilliseconds = action.DurationMilliseconds
                }
                : null
        };
    }

    private static string BuildReplayInstruction(GeneratedTaskAction action)
    {
        if (action.Kind == "input")
        {
            var inputTarget = DescribeInputTarget(action.Selector!, action.Description);
            return action.InputValue!.Length == 0
                ? RenderReplayInstruction(
                    "instruction-clear-input",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["INPUT_TARGET"] = inputTarget
                    })
                : RenderReplayInstruction(
                    "instruction-enter-input",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["INPUT_VALUE"] = SerializeString(action.InputValue),
                        ["INPUT_TARGET"] = inputTarget
                    });
        }

        if (action.Kind == "swipe")
        {
            return RenderReplayInstruction(
                "instruction-swipe",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["START_X"] = FormatNormalizedCoordinate(action.StartNormalizedX),
                    ["START_Y"] = FormatNormalizedCoordinate(action.StartNormalizedY),
                    ["END_X"] = FormatNormalizedCoordinate(action.EndNormalizedX),
                    ["END_Y"] = FormatNormalizedCoordinate(action.EndNormalizedY),
                    ["DURATION_MILLISECONDS"] = action.DurationMilliseconds.ToString(
                        CultureInfo.InvariantCulture)
                });
        }

        if (action.Selector is null)
        {
            return RenderReplayInstruction(
                "instruction-coordinate-tap",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NORMALIZED_X"] = FormatNormalizedCoordinate(action.StartNormalizedX),
                    ["NORMALIZED_Y"] = FormatNormalizedCoordinate(action.StartNormalizedY)
                });
        }

        var automationId = ReadString(action.Selector, "automationId");
        if (automationId is not null)
        {
            return RenderReplayInstruction(
                "instruction-automation-tap",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AUTOMATION_ID"] = SerializeString(automationId)
                });
        }

        var text = ReadString(action.Selector, "text");
        var type = ReadString(action.Selector, "type");
        var target = type is null ? "control" : type;
        return RenderReplayInstruction(
            "instruction-text-tap",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TARGET_TYPE"] = target,
                ["TARGET_TEXT"] = SerializeString(text ?? action.Description)
            });
    }

    private static string FormatNormalizedCoordinate(double? value)
        => value.GetValueOrDefault().ToString("0.######", CultureInfo.InvariantCulture);

    private static string DescribeInputTarget(JsonObject selector, string fallback)
    {
        var automationId = ReadString(selector, "automationId");
        if (automationId is not null)
        {
            return RenderReplayInstruction(
                "input-target-automation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AUTOMATION_ID"] = SerializeString(automationId)
                });
        }

        var text = ReadString(selector, "text");
        if (text is not null)
        {
            return RenderReplayInstruction(
                "input-target-label",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LABEL"] = SerializeString(text)
                });
        }

        var ancestorAutomationId = ReadString(selector, "ancestorAutomationId");
        if (ancestorAutomationId is not null)
        {
            return RenderReplayInstruction(
                "input-target-ancestor",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["AUTOMATION_ID"] = SerializeString(ancestorAutomationId)
                });
        }

        return fallback;
    }

    private static string RenderReplayInstruction(
        string sectionName,
        IReadOnlyDictionary<string, string> values)
        => EmbeddedTextResource.RenderSection(
            "Runtime/Tasks/Resources/replay-prompts.md",
            sectionName,
            values).TrimEnd();

    private static string DescribeOrientation(string orientation)
        => orientation switch
        {
            "N" => "up",
            "NE" => "up and right",
            "E" => "right",
            "SE" => "down and right",
            "S" => "down",
            "SW" => "down and left",
            "W" => "left",
            "NW" => "up and left",
            _ => orientation
        };

    private static string ResolveOrientation(double dx, double dy)
    {
        var horizontal = Math.Abs(dx) >= Math.Abs(dy) * 0.4
            ? dx >= 0 ? "E" : "W"
            : string.Empty;
        var vertical = Math.Abs(dy) >= Math.Abs(dx) * 0.4
            ? dy >= 0 ? "S" : "N"
            : string.Empty;
        return (vertical + horizontal) switch
        {
            "NE" => "NE",
            "NW" => "NW",
            "SE" => "SE",
            "SW" => "SW",
            "N" => "N",
            "S" => "S",
            "E" => "E",
            "W" => "W",
            _ => Math.Abs(dx) >= Math.Abs(dy) ? dx >= 0 ? "E" : "W" : dy >= 0 ? "S" : "N"
        };
    }

    private static string SerializeString(string value)
        => JsonSerializer.Serialize(value);

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static int ReadInteger(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var number)
            ? number
            : 0;

    private static string SanitizeComment(string value)
        => value.Replace("*/", "* /", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Slugify(string value)
    {
        var characters = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = string.Join(
            '-',
            new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "recorded-workflow" : slug;
    }

    private sealed record SelectorCandidate(
        JsonObject Selector,
        int Score,
        string Description,
        string Key,
        string? VisualTreeSnapshotId);

    private sealed record GeneratedTaskAction(
        string Kind,
        JsonObject? Selector,
        long DurationMilliseconds,
        double? Length,
        string? Orientation,
        string Description,
        DateTimeOffset CapturedAtUtc,
        DateTimeOffset OrderAtUtc,
        string? TargetKey,
        string? InputValue,
        string? VisualTreeSnapshotId,
        double? StartNormalizedX,
        double? StartNormalizedY,
        double? EndNormalizedX,
        double? EndNormalizedY);

    private sealed record ReplayInputNodeState(
        string Identity,
        JsonObject Selector,
        string Description,
        string Value,
        bool HasStableSelector);

    private sealed record InputTreeReadResult(
        IReadOnlyDictionary<string, ReplayInputNodeState> States,
        int OmittedValueCount)
    {
        public static InputTreeReadResult Empty { get; } = new(
            new Dictionary<string, ReplayInputNodeState>(StringComparer.Ordinal),
            0);
    }
}

internal sealed record TimelineExtractedAction(
    string Kind,
    JsonObject? Selector,
    long DurationMilliseconds,
    string Description,
    DateTimeOffset CapturedAtUtc,
    string? InputValue,
    double? StartNormalizedX,
    double? StartNormalizedY,
    double? EndNormalizedX,
    double? EndNormalizedY);
