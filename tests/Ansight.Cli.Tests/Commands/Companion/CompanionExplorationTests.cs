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
