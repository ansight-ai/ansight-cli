using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

public static partial class TimelineTaskExtractor
{
    private static IReadOnlyList<HostInputAction> ReadHostInputs(
        AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc,
        ICollection<string> diagnostics)
    {
        var inputs = snapshot.ApplicationEvents.Select(HostInputAction.Read)
            .OfType<HostInputAction>()
            .Where(action => action.StartedAtUtc <= endUtc && action.CompletedAtUtc >= startUtc)
            .DistinctBy(action => action.ActionId)
            .OrderBy(action => action.StartedAtUtc).ThenBy(action => action.CompletedAtUtc).ToArray();
        foreach (var action in inputs.Where(action => action.Status != "succeeded"))
            diagnostics.Add($"Host input {action.Kind} ({action.ActionId}) was {action.Status}; it was not generated. Review the resulting app state.");
        foreach (var action in inputs.Where(action => action.StartedAtUtc < startUtc || action.CompletedAtUtc > endUtc))
            diagnostics.Add($"Host input {action.Kind} ({action.ActionId}) crosses the selected range boundary; expand the range to include the complete action.");
        return inputs;
    }

    private static void AddHostInputActions(AppSessionSnapshot snapshot, IReadOnlyList<HostInputAction> inputs,
        ICollection<GeneratedTaskAction> actions, ICollection<string> diagnostics, bool coordinateFallback,
        DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        foreach (var input in inputs)
        {
            // Values come only from retained, redacted trees; partial delivery is never a complete action.
            if (input.Status != "succeeded" || input.Kind == "type"
                || input.StartedAtUtc < startUtc || input.CompletedAtUtc > endUtc) continue;
            SelectorCandidate? selected = null;
            if (input.Kind == "tap")
            {
                if (input.NormalizedX is not { } x || input.NormalizedY is not { } y
                    || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1)
                {
                    diagnostics.Add($"Host tap {input.ActionId} has no valid viewport point; it was not generated.");
                    continue;
                }
                var linkedTrees = snapshot.VisualTreeSnapshots
                    .Where(tree => input.EvidenceActionId is not null && tree.ActionId == input.EvidenceActionId
                        && tree.CapturedAtUtc <= input.StartedAtUtc).ToArray();
                var trees = linkedTrees.Length > 0 ? linkedTrees : snapshot.VisualTreeSnapshots
                    .Where(tree => tree.CapturedAtUtc <= input.StartedAtUtc
                        && input.StartedAtUtc - tree.CapturedAtUtc <= targetWindow)
                    .OrderByDescending(tree => tree.CapturedAtUtc).Take(1).ToArray();
                selected = trees.SelectMany(tree => TapTargetMatcher.FindVisualTreeTapTargetCandidates(
                        snapshot, tree, new NormalizedPoint(x, y)))
                    .Select(TryCreateSelectorCandidate).OfType<SelectorCandidate>()
                    .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selected is null)
                {
                    diagnostics.Add($"Host tap {input.ActionId} has no stable recorded UI selector; "
                        + (coordinateFallback ? "replay uses its recorded viewport point." : "it was not generated."));
                    if (!coordinateFallback) continue;
                }
            }
            actions.Add(new GeneratedTaskAction(input.Kind, selected?.Selector,
                (long)(input.CompletedAtUtc - input.StartedAtUtc).TotalMilliseconds, null, null,
                selected?.Description ?? (input.Kind == "tap" ? "recorded viewport point"
                    : input.Kind == "keyboard.press" ? "press Enter in the focused input" : "submit the focused input"),
                input.StartedAtUtc, input.StartedAtUtc, selected?.Key, null, selected?.VisualTreeSnapshotId,
                input.NormalizedX, input.NormalizedY, null, null));
        }
    }

    private static bool IsRecordedHostTap(TouchGestureSegment gesture, IReadOnlyList<HostInputAction> inputs,
        ISet<string> matchedInputs)
    {
        if (gesture.Kind is not ("tap" or "incomplete") || gesture.PointerIds.Count != 1
            || TouchReviewGeometry.NormalizeTouchAction(gesture.Touches[^1].Action) == "cancel") return false;
        var point = TouchReviewGeometry.ResolveGesturePoint(gesture);
        if (!point.HasValue) return false;
        // Match at most one SDK gesture to each delivery attempt. Failed attempts remain review items. Time alone is not enough:
        // preserve other taps, drags and cancelled gestures even inside the same interval.
        var match = inputs.Where(input => input.Kind == "tap" && !matchedInputs.Contains(input.ActionId)
                && input.NormalizedX.HasValue && input.NormalizedY.HasValue
                && gesture.StartUtc >= input.StartedAtUtc.AddMilliseconds(-50)
                && gesture.EndUtc <= input.CompletedAtUtc.AddMilliseconds(50)
                && Math.Abs(point.Value.X - input.NormalizedX.Value) <= 0.01
                && Math.Abs(point.Value.Y - input.NormalizedY.Value) <= 0.01)
            .OrderBy(input => Math.Abs((gesture.StartUtc - input.StartedAtUtc).TotalMilliseconds)).FirstOrDefault();
        return match is not null && matchedInputs.Add(match.ActionId);
    }
}
