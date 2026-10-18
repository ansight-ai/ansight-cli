using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.SimulatorAgent.OpenAi;
using Ansight.Host.Workspaces.Cloud;
using Ansight.Host.Workspaces.Execution;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ScreenshotEvaluationRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsesConfiguredProviderWithNoActionToolsAndCompletesMetering(bool cancel)
    {
        var gateway = new Gateway();
        var client = new Client { Cancel = cancel };
        using var bitmap = new SKBitmap(10, 10);
        bitmap.Erase(SKColors.Purple);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = new InspectionImage(encoded.ToArray(), 10, 10, "hash", "frame", null, DateTimeOffset.UtcNow);
        var request = new ScreenshotEvaluationRequest("app", "/workspace", image, image,
            new JsonArray(new JsonObject { ["id"] = "purple", ["description"] = "Must be purple." }), "balanced", null);
        var runner = new ScreenshotEvaluationRunner(gateway, () => client);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.EvaluateAsync(request, CancellationToken.None));
        else
        {
            var result = await runner.EvaluateAsync(request, CancellationToken.None);
            Assert.Equal("resolved-model", result.Model);
            Assert.Equal("resolved-revision", result.ConfigurationRevision);
        }
        Assert.Equal("balanced", gateway.Request!.Reasoning);
        Assert.False(gateway.Request.IsSessionSummary);
        Assert.Equal(1, gateway.Request.ValidationAssertionCount);
        Assert.Same(gateway.Transport, client.Request!.Transport);
        Assert.Equal("resolved-model", client.Request.Model);
        Assert.Empty(client.Request.Tools);
        Assert.Contains("untrusted data", client.Request.Instructions);
        Assert.Equal(2, client.Request.Input[0]!["content"]!.AsArray().Count(item => item!["type"]!.GetValue<string>() == "input_image"));
        Assert.Equal(cancel ? "cancelled" : "succeeded", gateway.Completion!.Status);
        Assert.Equal(cancel ? 0 : 1, gateway.Completion.ModelPassCount);
    }

    private sealed class Gateway : IWorkspaceTestRunGateway
    {
        public IModelExecutionTransport Transport { get; } = new Transport();
        public WorkspaceTestRunPreparationRequest? Request { get; private set; }
        public WorkspaceTestRunMeterCompletion? Completion { get; private set; }
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(WorkspaceTestRunPreparation.WithTransport(Transport, Guid.NewGuid(), 1, Guid.NewGuid(), "Test") with
            { ReasoningConfiguration = new AgentReasoningConfiguration("balanced", "resolved-model", "medium", "resolved-revision") });
        }
        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion, CancellationToken cancellationToken = default)
        { Completion = completion; return Task.FromResult(WorkspaceTestRunMeterResult.Success()); }
    }
    private sealed class Transport : IModelExecutionTransport
    {
        public string Description => "Test provider";
        public bool SupportsWebSockets => false;
        public ValueTask<string> ResolveAccessKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult("test-access");
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string DescribeFailure(int statusCode, string responseContent) => "Test failure";
    }
    private sealed class Client : IOpenAiClient
    {
        public bool Cancel { get; init; }
        public OpenAiRequest? Request { get; private set; }
        public Task<OpenAiTurn> CreateResponseAsync(OpenAiRequest request, CancellationToken token)
        {
            Request = request;
            if (Cancel) throw new OperationCanceledException(token);
            return Task.FromResult(new OpenAiTurn("response", "resolved-model", new JsonArray(), [], "{}", SimulatorAgentTokenUsage.Empty));
        }
        public void Dispose() { }
    }
}
