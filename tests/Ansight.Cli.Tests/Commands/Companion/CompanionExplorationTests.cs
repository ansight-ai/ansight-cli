using Ansight.RemoteSimulator.Core.Annotations;
namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionExplorationTests
{
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
}
