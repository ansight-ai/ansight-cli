using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure.Logging;

namespace Ansight.Host.Transcription;

internal static class SessionRecordingTranscript
{
    internal const string Kind = "recordingTranscript";
    private const int MaximumSegments = 2048;
    private const int MaximumSegmentCharacters = 12000;
    private static readonly ILogger log = Logger.Create();

    internal static OperationResult SaveSegment(
        RuntimeCoordinator runtime,
        string sessionId,
        Guid recordingId,
        string channel,
        SessionTranscriptSegment segment)
    {
        var text = segment.Text?.Trim();
        var validText = text is { Length: > 0 and <= MaximumSegmentCharacters };
        var validTime = segment.StartUtc != default
            && segment.EndUtc > segment.StartUtc
            && segment.EndUtc - segment.StartUtc <= TimeSpan.FromMinutes(2)
            && segment.EndUtc <= DateTimeOffset.UtcNow.AddSeconds(5);
        var validId = segment.SegmentId is { Length: > 0 and <= 100 }
            && segment.SegmentId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
        if (!validText || !validTime || !validId)
        {
            return OperationResult.Failure("The transcript segment or its timestamp is invalid.");
        }

        var id = RecordingKey(recordingId);
        string? error = null;
        var saved = runtime.UpdateRecordingTranscript(sessionId, id, current =>
        {
            var segments = current?.TranscriptSegments ?? [];
            var existing = segments.FirstOrDefault(item => item.SegmentId == segment.SegmentId);
            if (existing is not null)
            {
                if (existing != segment with { Text = text! })
                {
                    error = "The transcript segment already exists with different content.";
                }

                return current;
            }

            var canAppend = current?.CompletedUtc is null
                && segments.Count < MaximumSegments
                && (current is null || current.AgentId == channel);
            if (!canAppend)
            {
                error = "This transcript can no longer accept speech.";
                return current;
            }

            var ordered = segments.Append(segment with { Text = text! })
                .OrderBy(item => item.StartUtc)
                .ThenBy(item => item.SegmentId, StringComparer.Ordinal)
                .ToArray();
            return new SessionAnalysisRecord
            {
                AnalysisId = id,
                AnalysisKind = Kind,
                AgentId = channel,
                StartedUtc = ordered[0].StartUtc,
                Success = true,
                TranscriptSegments = ordered
            };
        });

        return error is null ? saved : OperationResult.Failure(error);
    }

    internal static OperationResult Complete(RuntimeCoordinator runtime, string sessionId, Guid recordingId)
    {
        var id = RecordingKey(recordingId);
        SessionAnalysisRecord? transcript = null;
        var saved = runtime.UpdateRecordingTranscript(sessionId, id, current =>
        {
            if (current is null || current.CompletedUtc is not null)
            {
                return current;
            }

            transcript = current with
            {
                CompletedUtc = current.TranscriptSegments!.Max(segment => segment.EndUtc),
                StatusMessage = "Transcript saved."
            };
            return transcript;
        });

        if (saved.IsSuccess && transcript?.TranscriptSegments is { Count: > 0 } segments)
        {
            _ = Task.Run(() => InferAsync(runtime, sessionId, recordingId, transcript, segments));
        }

        return saved;
    }

    private static async Task InferAsync(
        RuntimeCoordinator runtime,
        string sessionId,
        Guid recordingId,
        SessionAnalysisRecord transcript,
        IReadOnlyList<SessionTranscriptSegment> segments)
    {
        var id = transcript.AnalysisId;
        var status = "Transcript saved. Annotation inference did not complete.";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runtime.FeatureLifetime);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: timeout.Token)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The recording session was not found.");
            var start = segments[0].StartUtc.AddSeconds(-5);
            var end = transcript.CompletedUtc!.Value.AddSeconds(5);
            var section = LocalSessionSummaryRunner.SelectSection(snapshot, start, end, allowEmpty: true);
            var analysis = await LocalSessionSummaryRunner.RunAsync(runtime, section, teamId: null, timeout.Token,
                recordingTranscript: segments).ConfigureAwait(false);

            var findings = System.Text.Json.JsonSerializer.Deserialize<Finding[]>(analysis.FinalResponse ?? "[]",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
            var written = 0;
            var byId = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in findings.Take(12))
            {
                var hasContent = finding?.Label is { Length: > 0 and <= 200 }
                    && finding.Notes is { Length: > 0 and <= 2000 };
                if (!hasContent || finding!.SegmentIds is not { Length: > 0 and <= 12 })
                {
                    continue;
                }

                var excerpts = finding.SegmentIds
                    .Where(segmentId => segmentId is not null && byId.ContainsKey(segmentId))
                    .Distinct(StringComparer.Ordinal)
                    .Select(segmentId => byId[segmentId])
                    .OrderBy(segment => segment.StartUtc)
                    .ToArray();
                if (excerpts.Length != finding.SegmentIds.Length || !seen.Add(excerpts[0].SegmentId))
                {
                    continue;
                }

                var annotation = new SessionAnnotation
                {
                    AnnotationId = $"inferred-{recordingId:N}-{excerpts[0].SegmentId}",
                    Source = "transcript.inferred",
                    CaptureGroupId = recordingId.ToString("D"),
                    Label = finding.Label!,
                    Notes = finding.Notes,
                    StartUtc = excerpts[0].StartUtc,
                    EndUtc = excerpts.Max(item => item.EndUtc),
                    CustomData = new JsonObject
                    {
                        ["kind"] = "inferred",
                        ["transcriptId"] = id,
                        ["segmentIds"] = new JsonArray(excerpts.Select(item => (JsonNode?)JsonValue.Create(item.SegmentId)).ToArray())
                    }
                };
                if (runtime.SessionEditing.UpsertAnnotation(sessionId, annotation).IsSuccess)
                {
                    written++;
                }
            }

            status = written > 0
                ? $"Generated {written} inferred {(written == 1 ? "annotation" : "annotations")}."
                : "No points of interest were found.";
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException
            or IOException or HttpRequestException or ArgumentException or System.Text.Json.JsonException)
        {
            log.Warning($"Recording annotation inference ended with {exception.GetType().Name}.");
        }
        finally
        {
            runtime.UpdateRecordingTranscript(sessionId, id, current =>
                current is null ? null : current with { StatusMessage = status });
        }
    }

    internal static string RecordingKey(Guid recordingId) => $"transcript-{recordingId:N}";

    private sealed record Finding(string? Label, string? Notes, string[]? SegmentIds);
}
