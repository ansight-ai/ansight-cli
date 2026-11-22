using Ansight.Host;
using Ansight.Host.Transcription;

namespace Ansight.Cli.Commands.Session;

internal static class SessionTranscriptCommand
{
    internal static async Task<int> RunAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(3, "ansight session transcript <session-id>");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var start = SessionCommands.ParseOptionalTimestamp(arguments.GetOption("start"), "start");
        var end = SessionCommands.ParseOptionalTimestamp(arguments.GetOption("end"), "end");
        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            throw new CliUsageException("--start must be at or before --end.");
        }

        var session = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return SessionCommands.WriteSessionNotFound(sessionId, output);
        }

        var recordingId = arguments.GetOption("recording")?.Trim();
        var offset = arguments.GetIntOption("offset", 0, 0, int.MaxValue);
        var limit = arguments.GetIntOption("limit", 200, 1, 10_000);
        var page = ReadPage(session, offset, limit, recordingId, start, end);

        output.Write(new
        {
            schema = "ansight.session-transcript/v1",
            sessionId,
            page.TotalCount,
            page.NextOffset,
            page.Segments
        }, () => page.Segments.Count == 0
            ? "No transcript segments matched."
            : string.Join(Environment.NewLine, page.Segments.Select(segment =>
                $"{segment.StartUtc:O} – {segment.EndUtc:O} [{segment.Channel} · {segment.RecordingId}] {segment.Text}"))
                + (page.NextOffset is { } next ? $"{Environment.NewLine}Continue with --offset {next}." : ""));

        return CliExitCodes.Success;
    }

    internal static TranscriptPage ReadPage(
        AppSessionSnapshot session,
        int offset,
        int limit,
        string? recordingId = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var lines = session.Analyses
            .Where(recording => recording.AnalysisKind == SessionRecordingTranscript.Kind)
            .SelectMany(recording =>
            {
                var captureGroupId = GetCaptureGroupId(recording.AnalysisId);
                var matchesRecording = recordingId is null
                    || string.Equals(captureGroupId, recordingId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(recording.AnalysisId, recordingId, StringComparison.OrdinalIgnoreCase);
                if (!matchesRecording)
                {
                    return Enumerable.Empty<TranscriptLine>();
                }

                return (recording.TranscriptSegments ?? [])
                    .Where(segment => (!start.HasValue || segment.EndUtc >= start.Value)
                        && (!end.HasValue || segment.StartUtc <= end.Value))
                    .Select(segment => new TranscriptLine(
                        captureGroupId, recording.AnalysisId, recording.AgentId,
                        segment.SegmentId, segment.StartUtc, segment.EndUtc, segment.Text));
            })
            .OrderBy(segment => segment.StartUtc)
            .ThenBy(segment => segment.RecordingId, StringComparer.Ordinal)
            .ThenBy(segment => segment.SegmentId, StringComparer.Ordinal)
            .ToArray();

        var segments = lines.Skip(offset).Take(limit).ToArray();
        var hasMore = offset < lines.Length && segments.Length < lines.Length - offset;
        return new TranscriptPage(lines.Length, hasMore ? offset + segments.Length : null, segments);
    }

    private static string GetCaptureGroupId(string transcriptId)
    {
        const string Prefix = "transcript-";
        return transcriptId.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(transcriptId.AsSpan(Prefix.Length), "N", out var recordingId)
            ? recordingId.ToString("D")
            : transcriptId;
    }

    internal sealed record TranscriptPage(int TotalCount, int? NextOffset, IReadOnlyList<TranscriptLine> Segments);

    internal sealed record TranscriptLine(
        string RecordingId,
        string TranscriptId,
        string Channel,
        string SegmentId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Text);
}
