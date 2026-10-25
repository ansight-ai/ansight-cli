namespace Ansight.Host.Runtime.Tasks;

internal sealed record SessionSummaryVideoFrame(SessionVideoRecording Recording,
    DateTimeOffset CapturedAtUtc, long PositionUs, string Reason, int Priority);

internal static class SessionSummaryVideoFrames
{
    // Keep the scoped runner-summary contract: at most five attached visual frames.
    internal const int MaximumFrames = 5;

    internal static IReadOnlyList<SessionSummaryVideoFrame> Select(AppSessionSnapshot snapshot)
    {
        var candidates = new List<SessionSummaryVideoFrame>();
        foreach (var recording in snapshot.VideoRecordings.OrderBy(item => item.StartedUtc))
        {
            recording.Validate();
            var start = recording.StartedUtc > snapshot.CreatedUtc ? recording.StartedUtc : snapshot.CreatedUtc;
            var end = recording.EndedUtc < snapshot.LastUpdatedUtc ? recording.EndedUtc : snapshot.LastUpdatedUtc;
            if (end <= start) continue;
            // The end of a recording is exclusive. Stay within its final display sample.
            var last = end.AddTicks(-10);
            Add(start, "recording start", 90);
            Add(last, "recording outcome", 100);
            foreach (var annotation in snapshot.Annotations)
            {
                Add(annotation.StartUtc, "annotation", 85);
                if (annotation.EndUtc is { } annotationEnd) Add(annotationEnd, "annotation outcome", 85);
            }
            foreach (var log in snapshot.Logs.Where(log => log.Priority is LogPriority.Error or LogPriority.Fatal))
                Add(log.TimestampUtc, "error", 80);
            foreach (var item in snapshot.ApplicationEvents)
            {
                var text = item.EventType + " " + item.Label;
                if (new[] { "navigation", "screen", "page", "ready", "loaded", "error", "failed" }
                    .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    Add(item.CapturedAtUtc.AddMilliseconds(250), "screen/event outcome", 70);
            }
            foreach (var touch in snapshot.Touches)
                Add(touch.CapturedAtUtc.AddMilliseconds(450), "after interaction", 60);
            for (var index = 1; index < 5; index++)
                Add(start.AddTicks((last - start).Ticks * index / 5), "timeline coverage", 20);

            void Add(DateTimeOffset timestamp, string reason, int priority)
            {
                if (timestamp < start || timestamp > last) return;
                candidates.Add(new(recording, timestamp, (timestamp - recording.StartedUtc).Ticks / 10, reason, priority));
            }
        }

        var selected = new List<SessionSummaryVideoFrame>();
        // Ensure the overall initial and final visual states survive a busy event stream.
        if (candidates.Count == 0) return selected;
        selected.Add(candidates.MinBy(item => item.CapturedAtUtc)!);
        var final = candidates.MaxBy(item => item.CapturedAtUtc)!;
        if (final != selected[0]) selected.Add(final);
        while (selected.Count < MaximumFrames)
        {
            var next = candidates.Where(candidate => !selected.Any(item =>
                    item.Recording.RecordingId == candidate.Recording.RecordingId
                    && Math.Abs((item.CapturedAtUtc - candidate.CapturedAtUtc).TotalMilliseconds) < 500))
                .OrderByDescending(item => item.Priority)
                .ThenByDescending(item => selected.Min(chosen => Math.Abs((chosen.CapturedAtUtc - item.CapturedAtUtc).TotalSeconds)))
                .ThenBy(item => item.CapturedAtUtc)
                .FirstOrDefault();
            if (next is null) break;
            selected.Add(next);
        }
        return selected.OrderBy(item => item.CapturedAtUtc).ToArray();
    }
}
