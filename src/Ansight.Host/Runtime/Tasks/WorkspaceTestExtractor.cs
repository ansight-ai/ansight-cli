using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Models.Session;
using Ansight.Host.Workspaces.Catalog;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable Ansight workspace test derived from a recorded session interval.</summary>
public sealed record WorkspaceTestExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics);

public static class WorkspaceTestExtractor
{
    public static WorkspaceTestExtraction Extract(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title,
        IReadOnlyList<string>? assertions = null,
        string? validationPrompt = null,
        IReadOnlyList<string>? taskSectionIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var journey = TimelineTaskExtractor.Extract(
            snapshot, startUtc, endUtc, title, includeCoordinateFallbackTaps: true);
        var diagnostics = new List<string>(journey.Diagnostics);
        var outcomes = assertions?
            .Where(static assertion => !string.IsNullOrWhiteSpace(assertion))
            .Select(static assertion => assertion.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var needsOutcomeReview = false;
        if (outcomes.Length == 0)
        {
            var observedOutcome = FindNewFinalLabel(
                snapshot, startUtc, endUtc, journey.ReplaySteps.LastOrDefault()?.CapturedAtUtc);
            if (observedOutcome is not null)
            {
                outcomes = [$"The final screen visibly shows {JsonSerializer.Serialize(observedOutcome)}."];
                diagnostics.Add("The final-state assertion was inferred from a recorded UI label; confirm that it represents the intended product outcome.");
            }
            else
            {
                outcomes = [$"The app visibly reaches the intended outcome of {JsonSerializer.Serialize(title.Trim())}."];
                diagnostics.Add("No distinct final UI label was found. Replace the generic outcome assertion with an observable product result before running this test.");
                needsOutcomeReview = true;
            }
        }

        var prompt = new StringBuilder()
            .Append("In the ").Append(snapshot.AppId).AppendLine(" app, complete the following user journey using the visible UI.")
            .AppendLine("Start from the state required for this journey and adapt to minor UI changes.");
        if (taskSectionIds is { Count: > 0 })
        {
            var selectedIds = taskSectionIds.ToHashSet(StringComparer.Ordinal);
            var sections = snapshot.Annotations
                .Where(annotation => selectedIds.Contains(annotation.AnnotationId)
                    && annotation.EndUtc > annotation.StartUtc
                    && annotation.StartUtc >= startUtc
                    && annotation.EndUtc <= endUtc
                    && !string.IsNullOrWhiteSpace(annotation.Label))
                .OrderBy(annotation => annotation.StartUtc)
                .ToArray();
            if (sections.Length != selectedIds.Count)
            {
                throw new ArgumentException("Every selected task section must be a labelled range annotation inside the test period.");
            }

            prompt.AppendLine("The human marked these core sections as candidates for reusable automated tasks. Preserve their order and intent:");
            for (var index = 0; index < sections.Length; index++)
            {
                var section = sections[index];
                prompt.Append(index + 1).Append(". ").Append(section.Label.Trim());
                if (!string.IsNullOrWhiteSpace(section.Notes)) prompt.Append(" — ").Append(section.Notes.Trim());
                prompt.AppendLine();
            }
        }
        prompt.AppendLine("The recorded sequence is:");
        if (journey.ReplaySteps.Count == 0)
        {
            prompt.AppendLine("1. Review the selected replay and describe the missing interaction steps before running this test.");
            diagnostics.Add("No replayable interactions were found; complete the journey prompt before running this test.");
        }
        else
        {
            for (var index = 0; index < journey.ReplaySteps.Count; index++)
            {
                var step = journey.ReplaySteps[index];
                var instruction = step.Kind == "input"
                    ? "Enter an appropriate test value in the recorded input field. Review the value needed for this journey."
                    : step.Instruction;
                prompt.Append(index + 1).Append(". ").AppendLine(instruction);
                if (step.Kind == "input")
                {
                    diagnostics.Add($"Input at {step.CapturedAtUtc:O} needs a test value; captured text was omitted from the YAML test.");
                }
            }
        }

        var hasOmittedInput = journey.ReplaySteps.Any(static step => step.Kind == "input");
        var source = new StringBuilder()
            .AppendLine("# REVIEW: Confirm the starting state, recorded steps, and final-state assertions before running.")
            .Append(hasOmittedInput ? "# REVIEW: Recorded input values were omitted; supply suitable test values or secret aliases.\n" : string.Empty)
            .Append(needsOutcomeReview ? "# REVIEW: Replace the generic outcome assertion and enable this test.\n" : string.Empty)
            .Append(journey.ReplaySteps.Count == 0 ? "# REVIEW: Add the missing journey steps and enable this test.\n" : string.Empty)
            .AppendLine("schemaVersion: 1")
            .Append(needsOutcomeReview || journey.ReplaySteps.Count == 0 ? "enabled: false\n" : string.Empty)
            .Append("id: ").AppendLine(Quote(journey.SuggestedName))
            .Append("name: ").AppendLine(Quote(title.Trim()))
            .Append("appId: ").AppendLine(Quote(snapshot.AppId));
        AppendBlock(source, "prompt", prompt.ToString().TrimEnd());
        source.AppendLine("validation:");
        AppendBlock(source, "  prompt", string.IsNullOrWhiteSpace(validationPrompt)
            ? "Inspect the final app state through Ansight UI evidence and verify every assertion below."
            : validationPrompt.Trim());
        source.AppendLine("  assertions:");
        foreach (var outcome in outcomes)
        {
            source.Append("    - ").AppendLine(Quote(outcome));
        }

        // Parse with the same contract used by workspace test discovery.
        WorkspaceTestCatalog.Parse("ansight/tests", $"ansight/tests/{journey.SuggestedName}.yaml", source.ToString());
        return new WorkspaceTestExtraction(journey.SuggestedName, source.ToString(), journey.ReplaySteps.Count, diagnostics);
    }

    private static string? FindNewFinalLabel(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DateTimeOffset? lastActionUtc)
    {
        if (lastActionUtc is null) return null;
        var trees = snapshot.VisualTreeSnapshots
            .Where(tree => tree.CapturedAtUtc >= startUtc && tree.CapturedAtUtc <= endUtc)
            .OrderBy(tree => tree.CapturedAtUtc)
            .ToArray();
        if (trees.Length < 2) return null;
        var finalTree = trees.LastOrDefault(tree => tree.CapturedAtUtc >= lastActionUtc.Value);
        if (finalTree is null) return null;
        var initial = ReadLabels(trees[0].Payload).ToHashSet(StringComparer.Ordinal);
        return ReadLabels(finalTree.Payload)
            .Where(label => !initial.Contains(label))
            .OrderByDescending(static label => label.Length)
            .FirstOrDefault();
    }

    private static IEnumerable<string> ReadLabels(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            if (value["visible"] is JsonValue visibleValue
                && visibleValue.TryGetValue<bool>(out var visible)
                && !visible)
            {
                yield break;
            }
            foreach (var key in new[] { "label", "text" })
            {
                if (value[key] is JsonValue labelValue
                    && labelValue.TryGetValue<string>(out var label)
                    && !string.IsNullOrWhiteSpace(label)
                    && label.Trim().Length is >= 3 and <= 100
                    && !label.Contains('@')
                    && !label.Any(char.IsControl))
                {
                    yield return label.Trim();
                }
            }
            foreach (var child in value)
            {
                if (child.Key is "visual" or "properties") continue;
                foreach (var label in ReadLabels(child.Value)) yield return label;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                foreach (var label in ReadLabels(child)) yield return label;
            }
        }
    }

    private static void AppendBlock(StringBuilder source, string key, string value)
    {
        var indentation = new string(' ', key.Length - key.TrimStart().Length + 2);
        source.Append(key).AppendLine(": |-");
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            source.Append(indentation).AppendLine(line);
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
