namespace Ansight.Host.Trends;

public static class WorkspaceTrendsSpanResolver
{
    public static WorkspaceTrendsSpanResolution Resolve(
        AppSessionSnapshot snapshot,
        WorkspaceTrendsSpanDefinition span)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(span);
        var starts = ResolveAnchors(snapshot, span.Start);
        var ends = ResolveAnchors(snapshot, span.End);
        if (starts.Length == 0)
        {
            return Inconclusive($"Start {(span.Start.Log is null ? "event" : "log")} '{span.Start.Label}' was not captured for the trends span.");
        }
        if (ends.Length == 0)
        {
            return Inconclusive($"End {(span.End.Log is null ? "event" : "log")} '{span.End.Label}' was not captured for the trends span.");
        }

        var candidates = new List<WorkspaceTrendsSpanInstance>();
        var endsByGroup = ends
            .GroupBy(static appEvent => appEvent.GroupKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var nextEndIndexByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var start in starts)
        {
            if (!endsByGroup.TryGetValue(start.GroupKey, out var matchingEnds))
            {
                continue;
            }

            var nextEndIndex = nextEndIndexByGroup.GetValueOrDefault(start.GroupKey);
            while (nextEndIndex < matchingEnds.Length
                   && matchingEnds[nextEndIndex].CapturedAtUtc < start.CapturedAtUtc)
            {
                nextEndIndex++;
            }
            if (nextEndIndex >= matchingEnds.Length)
            {
                nextEndIndexByGroup[start.GroupKey] = nextEndIndex;
                continue;
            }

            var end = matchingEnds[nextEndIndex++];
            nextEndIndexByGroup[start.GroupKey] = nextEndIndex;
            var duration = end.CapturedAtUtc - start.CapturedAtUtc;
            if (duration <= TimeSpan.Zero || duration > span.MaximumDuration)
            {
                continue;
            }

            candidates.Add(new WorkspaceTrendsSpanInstance(
                candidates.Count,
                start.EventId,
                end.EventId,
                start.CapturedAtUtc.ToUniversalTime(),
                end.CapturedAtUtc.ToUniversalTime())
            {
                Group = start.Group
            });
        }

        if (candidates.Count == 0)
        {
            return Inconclusive(
                $"No completed '{span.Start.Label}' to '{span.End.Label}' interval was captured for the trends span.");
        }
        var candidateGroups = candidates
            .GroupBy(static instance => instance.Group ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        var invalidExactGroup = span.Selection == WorkspaceTrendsSpanSelection.ExactlyOne
            ? candidateGroups.FirstOrDefault(static group => group.Count() != 1)
            : null;
        if (invalidExactGroup is not null)
        {
            var groupContext = !string.IsNullOrEmpty(invalidExactGroup.Key)
                ? $" in group '{invalidExactGroup.Key}'"
                : string.Empty;
            return Inconclusive(
                $"The trends span required exactly one completed interval{groupContext} but resolved {invalidExactGroup.Count():N0}.");
        }

        var selected = candidateGroups
            .SelectMany(group => span.Selection switch
            {
                WorkspaceTrendsSpanSelection.FirstCompleted => group.Take(1),
                WorkspaceTrendsSpanSelection.LastCompleted => group.TakeLast(1),
                WorkspaceTrendsSpanSelection.ExactlyOne => group,
                WorkspaceTrendsSpanSelection.All => group,
                _ => group
            })
            .OrderBy(static instance => instance.StartUtc)
            .ThenBy(static instance => instance.StartEventId, StringComparer.Ordinal)
            .Select((instance, index) => instance with { InstanceIndex = index })
            .ToArray();
        var detectedGroupCount = candidateGroups.Count(static group => !string.IsNullOrEmpty(group.Key));
        var groupMessage = detectedGroupCount > 0
            ? $" across {detectedGroupCount:N0} event group(s)"
            : string.Empty;
        return new WorkspaceTrendsSpanResolution(
            WorkspaceTrendsStatus.Passed,
            $"Resolved {selected.Length:N0} completed trends span instance(s){groupMessage}.",
            selected);
    }

    private static GroupedSpanEvent[] ResolveAnchors(AppSessionSnapshot snapshot, WorkspaceEventAnchor anchor)
    {
        IEnumerable<GroupedSpanEvent> matches;
        if (anchor.Log is { } selector)
        {
            // Streams are authoritative when present; Logs is a legacy projection of the same entries.
            var stream = snapshot.LogStreams.FirstOrDefault(value => value.StreamId == selector.StreamId);
            var entries = stream?.Entries ?? snapshot.Logs.Where(entry => entry.StreamId == selector.StreamId);
            matches = entries.Select((entry, index) => new IndexedLog(entry, index))
                .Where(value => MatchesLog(value.Entry, selector))
                .Select(value => new GroupedSpanEvent(
                    $"log:{selector.StreamId}:{value.Entry.EventId ?? value.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    value.Entry.TimestampUtc, string.Empty, null));
        }
        else
        {
            matches = snapshot.ApplicationEvents.Where(value => Matches(value, anchor)).Select(CreateGroupedEvent);
        }
        return matches.OrderBy(value => value.CapturedAtUtc)
            .ThenBy(value => value.EventId, StringComparer.Ordinal).ToArray();
    }

    private static bool MatchesLog(LogEntry entry, WorkspaceTrendsLogSelector selector)
        => (selector.Match == "contains"
                ? entry.Message.Contains(selector.Message, StringComparison.Ordinal)
                : string.Equals(entry.Message, selector.Message, StringComparison.Ordinal))
           && (selector.Priority is null || entry.Priority.ToString() == selector.Priority)
           && (selector.Source is null || entry.Source == selector.Source)
           && (selector.Tag is null || entry.Tag == selector.Tag)
           && (selector.ProcessId is null || entry.ProcessId == selector.ProcessId);

    private static GroupedSpanEvent CreateGroupedEvent(SessionApplicationEvent appEvent)
    {
        var group = appEvent.Details.Trim();
        return new GroupedSpanEvent(
            appEvent.EventId,
            appEvent.CapturedAtUtc,
            group,
            string.IsNullOrEmpty(group) ? null : group);
    }

    private static bool Matches(SessionApplicationEvent appEvent, WorkspaceEventAnchor anchor)
        => string.Equals(appEvent.Label, anchor.Label, StringComparison.Ordinal)
           && (string.IsNullOrWhiteSpace(anchor.EventType)
               || string.Equals(appEvent.EventType, anchor.EventType, StringComparison.OrdinalIgnoreCase))
           && (!anchor.ChannelId.HasValue || appEvent.ChannelId == anchor.ChannelId.Value);

    private static WorkspaceTrendsSpanResolution Inconclusive(string message)
        => new(WorkspaceTrendsStatus.Inconclusive, message, []);

    private sealed record GroupedSpanEvent(
        string EventId,
        DateTimeOffset CapturedAtUtc,
        string GroupKey,
        string? Group);

    private sealed record IndexedLog(LogEntry Entry, int Index);
}
