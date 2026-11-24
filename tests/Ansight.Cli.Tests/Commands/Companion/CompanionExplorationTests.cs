using Ansight.Cli.Commands.Session;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Ansight.Host.Replay;
using Ansight.Host.Transcription;
using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionExplorationTests
{
    [Fact]
    public async Task CompletedSpeechOnlyRecordingCanStartATaskAndRetryWithoutDuplicateDelivery()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var recordingId = Guid.NewGuid();
        var sessionId = context.Session.SessionId;
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var segment = new SessionTranscriptSegment("1", start, start.AddSeconds(1), "Update the save button.");
        Assert.True(SessionRecordingTranscript.SaveSegment(context.Runtime, sessionId, recordingId,
            "Companion microphone", segment).IsSuccess);
        Assert.True(SessionRecordingTranscript.SaveSegment(context.Runtime, sessionId, Guid.NewGuid(),
            "Companion microphone", segment with { Text = "Unrelated speech." }).IsSuccess);
        var request = context.Submission(newTask: true) with
        {
            BatchId = recordingId.ToString("D"),
            AnnotationIds = [],
            IsRecording = true
        };

        var unfinished = await context.Source.SubmitAnnotationBatchAsync(request);
        Assert.False(unfinished.IsSuccess);
        Assert.Contains("Finish saving", unfinished.Message);
        Assert.DoesNotContain(context.Codex.Calls, call => call.Method == "turn/start");

        Assert.True(context.Runtime.UpdateRecordingTranscript(sessionId, SessionRecordingTranscript.RecordingKey(recordingId),
            current => current! with { CompletedUtc = segment.EndUtc }).IsSuccess);
        var submitted = await context.Source.SubmitAnnotationBatchAsync(request);
        var retry = await context.Source.SubmitAnnotationBatchAsync(request);
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal("new-thread", submitted.ProviderTaskId);
        Assert.Equal(submitted.TaskLink?.LinkId, retry.TaskLink?.LinkId);
        var prompt = Assert.Single(context.Codex.Calls, call => call.Method == "turn/start").Parameters.ToString();
        Assert.Contains($"--recording {recordingId:D}", prompt);
        var evidenceDirectory = Path.Combine(context.Path, "companion-agents", "evidence");
        var evidencePath = Assert.Single(Directory.GetFiles(evidenceDirectory, "annotations.json", SearchOption.AllDirectories));
        var evidence = await File.ReadAllTextAsync(evidencePath);
        Assert.Contains(segment.Text, evidence);
        Assert.DoesNotContain("Unrelated speech.", evidence);
    }

    [Fact]
    public async Task SpeechAndMarkingsSendTogetherAndRetryDoesNotDuplicate()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var speech = new SessionAnnotation
        {
            AnnotationId = "speech-1", Source = "companion.exploration", CaptureGroupId = "batch-1",
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddSeconds(2),
            Label = "Make this control smaller", Geometry = []
        };
        Assert.True(context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, speech).IsSuccess);
        var request = context.Submission() with { AnnotationIds = ["annotation-1", "speech-1"] };
        var first = await context.Source.SubmitAnnotationBatchAsync(request);
        var retry = await context.Source.SubmitAnnotationBatchAsync(request);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(first.TaskLink?.LinkId, retry.TaskLink?.LinkId);
        var call = Assert.Single(context.Codex.Calls, c => c.Method == "turn/start");
        Assert.Contains("Make this control smaller", call.Parameters.ToString());
        Assert.Contains("Recording ID", call.Parameters.ToString());
    }

    [Theory]
    [InlineData("companion.exploration", "other-batch")]
    [InlineData("simulator.remote.mobile", "batch-1")]
    public async Task SpeechCannotBypassBatchMembership(string source, string batch)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, new SessionAnnotation
        {
            AnnotationId = "speech-1", Source = source, CaptureGroupId = batch,
            StartUtc = DateTimeOffset.UtcNow, Label = "Do not send", Geometry = []
        });
        var result = await context.Source.SubmitAnnotationBatchAsync(context.Submission() with { AnnotationIds = ["speech-1"] });
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method is "turn/start" or "queue");
    }

    [Fact]
    public async Task FeedbackDeliveryIsOneTextMessageWithAnsightReferencesAndNoImages()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var start = DateTimeOffset.UtcNow;
        foreach (var annotation in new[]
        {
            new SessionAnnotation { AnnotationId = "speech-before", Source = "companion.exploration", CaptureGroupId = "batch-1",
                StartUtc = start, EndUtc = start.AddSeconds(2), Label = "Make this smaller", Geometry = [] },
            new SessionAnnotation { AnnotationId = "speech-after", Source = "companion.exploration", CaptureGroupId = "batch-1",
                StartUtc = start.AddSeconds(4), EndUtc = start.AddSeconds(6), Label = "and move it left.", Geometry = [] },
            new SessionAnnotation { AnnotationId = "screen-wiggle", Source = "companion.exploration", CaptureGroupId = "batch-1",
                StartUtc = start.AddSeconds(3), EndUtc = start.AddSeconds(4), Label = "Screen marking",
                Geometry = context.Session.Annotations[0].Geometry }
        }) Assert.True(context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, annotation).IsSuccess);
        var result = await context.Source.SubmitAnnotationBatchAsync(context.Submission() with
            { AnnotationIds = ["speech-after", "screen-wiggle", "speech-before"] });
        Assert.True(result.IsSuccess, result.Message);
        var call = Assert.Single(context.Codex.Calls, c => c.Method == "turn/start");
        var input = call.Parameters.GetProperty("input").EnumerateArray().ToArray();
        Assert.Single(input);
        Assert.Equal("text", input[0].GetProperty("type").GetString());
        var prompt = input[0].GetProperty("text").GetString()!;
        Assert.Contains("Make this smaller and move it left.", prompt);
        Assert.Contains("Screen marking Annotation ID: screen-wiggle", prompt);
        Assert.Contains("Nearby speech: [speech-before] Make this smaller [speech-after] and move it left.", prompt);
        Assert.Contains("Use Ansight to investigate the complete session", prompt);
        Assert.Contains("No screenshots are attached.", prompt);
        Assert.DoesNotContain("Marked screenshot:", prompt);
        Assert.Empty(Directory.GetFiles(Path.Combine(context.Path, "companion-agents", "evidence"), "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void FeedbackCorrelationsNeverCrossRecordingsOrGuessDistantSpeech()
    {
        var start = DateTimeOffset.UtcNow;
        var feedback = RecordingFeedback.Create([
            new() { AnnotationId = "speech", Source = "localPortal.exploration", CaptureGroupId = "other",
                StartUtc = start, Label = "Foreign speech", Geometry = [] },
            new() { AnnotationId = "distant", Source = "companion.exploration", CaptureGroupId = "one",
                StartUtc = start.AddSeconds(20), Label = "Distant speech", Geometry = [] },
            new() { AnnotationId = "wiggle", Source = "companion.exploration", CaptureGroupId = "one",
                StartUtc = start, Label = "Screen marking", Geometry = [new()
                { GeometryId = "geometry", FrameId = "frame", CapturedAtUtc = start,
                    Kind = SessionAnnotationGeometryKind.Point, X = 0.5, Y = 0.5 }] }
        ]);
        Assert.Empty(Assert.Single(feedback.Single(f => f.RecordingId == "one").Markings).SpeechAnnotationIds);
    }

    [Fact]
    public async Task RecordingSaveRequiresAnIssuedBatch()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var source = new CliRemoteAnnotationSource(context.Runtime);
        var result = await source.CreateAnnotationAsync(new RemoteAnnotationRequest("device", "speech", 0, 0, null, null,
            "Hello", "investigate", "unissued-batch", "frame-1", RecordingStartUtc: DateTimeOffset.UtcNow,
            RecordingEndUtc: DateTimeOffset.UtcNow.AddSeconds(1), RecordingItemId: "line-1"));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DesktopTranscriptUpdatesOneRecordingWithoutCreatingSpeechAnnotations()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var explorer = await context.Runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = explorer.ExplorerUrl };
        var sessionRoute = $"api/sessions/{context.Session.SessionId}";
        var recordingId = Guid.NewGuid().ToString("D");
        var recordingRoute = $"{sessionRoute}/recording-transcripts/{recordingId}";
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var first = new SessionTranscriptSegment("1", start, start.AddSeconds(1), "The button is disabled.");
        var second = new SessionTranscriptSegment("2", start.AddSeconds(1), start.AddSeconds(2), "Here is a second observation.");

        using var saved = await client.PostAsJsonAsync($"{recordingRoute}/segment", first);
        saved.EnsureSuccessStatusCode();
        using var retry = await client.PostAsJsonAsync($"{recordingRoute}/segment", first);
        retry.EnsureSuccessStatusCode();
        using var conflict = await client.PostAsJsonAsync($"{recordingRoute}/segment", first with { Text = "Different content" });
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        using var appended = await client.PostAsJsonAsync($"{recordingRoute}/segment", second);
        appended.EnsureSuccessStatusCode();

        var update = await client.GetFromJsonAsync<SessionLiveUpdate>($"{sessionRoute}/updates?analysisCount=1&transcriptRevision=1");
        var transcript = Assert.Single(update!.Analyses);
        Assert.Equal("recordingTranscript", transcript.AnalysisKind);
        Assert.Equal(2, transcript.TranscriptRevision);
        Assert.Equal(new[] { first, second }, transcript.TranscriptSegments);
        Assert.DoesNotContain(update.Annotations, item => item.Label == first.Text || item.Label == second.Text);

        var snapshot = await context.Runtime.Sessions.LoadSnapshotAsync(context.Session.SessionId);
        var page = SessionTranscriptCommand.ReadPage(snapshot!, offset: 0, limit: 1);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.NextOffset);
        var firstLine = Assert.Single(page.Segments);
        Assert.Equal(first.Text, firstLine.Text);
        Assert.Equal(recordingId, firstLine.RecordingId);
        Assert.Equal(transcript.AnalysisId, firstLine.TranscriptId);

        var filtered = SessionTranscriptCommand.ReadPage(snapshot!, offset: 0, limit: 10,
            recordingId: recordingId, start: first.EndUtc.AddMilliseconds(1));
        Assert.Equal(second.Text, Assert.Single(filtered.Segments).Text);
        Assert.Null(filtered.NextOffset);

        var remote = new CliRemoteRecordingSource(context.Runtime);
        Assert.True((await remote.GetAsync(context.Session.SessionId))!.HasTranscript);
        var response = await remote.ReviewAsync("transcript.list", context.Session.SessionId,
            JsonSerializer.SerializeToElement(new { offset = 1 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(response.Content);
        var mobileSegment = Assert.Single(document.RootElement.GetProperty("segments").EnumerateArray());
        Assert.Equal(second.Text, mobileSegment.GetProperty("text").GetString());

    }

    [Theory]
    [InlineData("transcript")]
    [InlineData("transcript-complete")]
    public async Task CompanionTranscriptRequiresAnIssuedBatch(string kind)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var source = new CliRemoteAnnotationSource(context.Runtime);
        var result = await source.CreateAnnotationAsync(new RemoteAnnotationRequest("device", kind, 0, 0, null, null,
            "Hello", "investigate", Guid.NewGuid().ToString("D"), "frame-1", RecordingStartUtc: DateTimeOffset.UtcNow,
            RecordingEndUtc: DateTimeOffset.UtcNow.AddSeconds(1), RecordingItemId: "line-1"));
        Assert.False(result.IsSuccess);
    }

}
