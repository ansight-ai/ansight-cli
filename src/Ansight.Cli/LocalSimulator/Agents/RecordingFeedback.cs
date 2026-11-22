using System.Text.RegularExpressions;

namespace Ansight.Cli.LocalSimulator;

internal sealed record FeedbackSpeech(string AnnotationId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Text);
internal sealed record FeedbackMark(string AnnotationId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    IReadOnlyList<string> FrameIds, IReadOnlyList<string> SpeechAnnotationIds);
internal sealed record RecordingFeedback(string RecordingId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string Transcript, IReadOnlyList<FeedbackSpeech> Speech, IReadOnlyList<FeedbackMark> Markings)
{
    internal const int ContextSeconds = 3;
    internal static bool IsRecording(SessionAnnotation annotation) =>
        annotation.Source is "localPortal.exploration" or "companion.exploration";
    internal static bool IsSpeech(SessionAnnotation annotation) => IsRecording(annotation)
        && (annotation.CustomData?["kind"]?.ToString() == "speech"
            || (annotation.Geometry.Count == 0 && annotation.CustomData?["kind"] is null));
    internal static string Text(SessionAnnotation annotation) => string.IsNullOrWhiteSpace(annotation.Notes)
        ? annotation.Label.Trim() : $"{annotation.Label.Trim()}\n{annotation.Notes.Trim()}";

    internal static IReadOnlyList<RecordingFeedback> Create(IReadOnlyList<SessionAnnotation> annotations) => annotations
        .Where(IsRecording)
        // Never correlate unrelated recordings, including older entries without a capture group.
        .GroupBy(a => string.IsNullOrEmpty(a.CaptureGroupId) ? a.AnnotationId : a.CaptureGroupId)
        .Select(group =>
        {
            var ordered = group.OrderBy(a => a.StartUtc).ThenBy(a => a.AnnotationId, StringComparer.Ordinal).ToArray();
            var speech = ordered.Where(IsSpeech).Select(a => new FeedbackSpeech(a.AnnotationId, a.StartUtc,
                a.EndUtc ?? a.StartUtc, Text(a))).ToArray();
            var marks = ordered.Where(a => a.Geometry.Count > 0).Select(a => new FeedbackMark(a.AnnotationId,
                a.StartUtc, a.EndUtc ?? a.StartUtc, a.Geometry.Select(g => g.FrameId).Distinct().ToArray(),
                speech.Where(s => s.StartUtc <= (a.EndUtc ?? a.StartUtc).AddSeconds(ContextSeconds)
                    && s.EndUtc >= a.StartUtc.AddSeconds(-ContextSeconds)).Select(s => s.AnnotationId).ToArray())).ToArray();
            var transcript = Regex.Replace(string.Join(" ", speech.Select(s => s.Text)), @"\s+([,.;:!?])", "$1");
            return new RecordingFeedback(group.Key!, ordered[0].StartUtc,
                ordered.Max(a => a.EndUtc ?? a.StartUtc), transcript, speech, marks);
        }).OrderBy(group => group.StartUtc).ThenBy(group => group.RecordingId, StringComparer.Ordinal).ToArray();
}
