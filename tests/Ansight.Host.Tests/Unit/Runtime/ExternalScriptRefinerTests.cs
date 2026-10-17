using Ansight.Host.Tests.TestSupport;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ExternalScriptRefinerTests
{
    private const string AppId = "com.example.app";
    private static readonly DateTimeOffset start = DateTimeOffset.Parse("2026-10-16T00:00:00Z");

    [Theory]
    [InlineData("maestro")]
    [InlineData("appium")]
    public async Task GenerationUsesReplayImagesAndEvidenceRepairsInvalidSourceAndMetersEveryPass(string format)
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var snapshot = CreateSnapshot();
        WriteScreenshots(runtime, snapshot);
        var valid = format == "maestro"
            ? $"appId: {AppId}\n---\n- tapOn: Continue\n- assertVisible: Done\n"
            : AppiumScriptExtractor.Extract(snapshot, start, start.AddSeconds(10), "Continue").Source;
        var invalid = format == "maestro"
            ? $"appId: {AppId}\n---\n- tapOn: [broken\n"
            : valid + "\nfunction (";
        var gateway = new Gateway(invalid, "```" + (format == "maestro" ? "yaml" : "javascript") + "\n" + valid + "```");
        var progress = new List<string>();

        var result = await ExternalScriptRefiner.RefineAsync(gateway, runtime, snapshot, start, start.AddSeconds(10),
            "Continue", valid, format, AgentReasoningModes.Balanced, null, environment.RootPath,
            CancellationToken.None, progress.Add, "Check the final confirmation");

        Assert.Equal(valid.Trim() + "\n", result.Source);
        Assert.Equal("test-model", result.Model);
        Assert.Equal(2, gateway.Transport.Requests.Count);
        var firstRequest = gateway.Transport.Requests[0];
        var content = firstRequest["input"]![0]!["content"]!.AsArray();
        Assert.Equal(2, content.OfType<JsonObject>().Count(item => item["type"]?.GetValue<string>() == "input_image"));
        var prompt = content[0]!["text"]!.GetValue<string>();
        Assert.Contains("first-control", prompt);
        Assert.Contains("last-control", prompt);
        Assert.Contains("Confirm checkout", prompt);
        Assert.Contains("Check the final confirmation", prompt);
        Assert.DoesNotContain("outside-control", prompt);
        Assert.DoesNotContain("Outside annotation", prompt);
        Assert.Contains("failed validation", gateway.Transport.Requests[1].ToJsonString());
        Assert.Contains(progress, message => message.Contains("Repairing"));
        Assert.Equal(AgentReasoningModes.Balanced, gateway.Request!.Reasoning);
        Assert.False(string.IsNullOrWhiteSpace(gateway.Request.Model));
        var completion = Assert.Single(gateway.Completions);
        Assert.Equal("succeeded", completion.Status);
        Assert.Equal(2, completion.ModelPassCount);
        Assert.Equal(30, completion.Tokens!.TotalTokens);
    }

    [Fact]
    public async Task InvalidDraftFailsAfterThreeAttemptsWithoutReturningTheSeed()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var gateway = new Gateway("appId: wrong.app\n---\n- tapOn: Continue\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => ExternalScriptRefiner.RefineAsync(gateway,
            runtime, CreateSnapshot(), start, start.AddSeconds(10), "Continue", "seed", "maestro",
            AgentReasoningModes.Fast, null, environment.RootPath, CancellationToken.None));
        Assert.Equal(3, gateway.Transport.Requests.Count);
        var completion = Assert.Single(gateway.Completions);
        Assert.Equal("failed", completion.Status);
        Assert.Equal(3, completion.ModelPassCount);
    }

    [Fact]
    public async Task MissingModelAuthorizationFailsAndClosesMeteredRun()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var gateway = new Gateway("unused");
        gateway.Transport.AccessKey = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalScriptRefiner.RefineAsync(gateway,
            runtime, CreateSnapshot(), start, start.AddSeconds(10), "Continue", "seed", "maestro",
            AgentReasoningModes.Fast, null, environment.RootPath, CancellationToken.None));
        Assert.Empty(gateway.Transport.Requests);
        Assert.Equal("failed", Assert.Single(gateway.Completions).Status);
    }

    [Fact]
    public async Task CancellationDoesNotRetryOrReturnAProgrammaticDraft()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var gateway = new Gateway("unused");
        gateway.Transport.CancelRequest = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalScriptRefiner.RefineAsync(gateway,
            runtime, CreateSnapshot(), start, start.AddSeconds(10), "Continue", "seed", "maestro",
            AgentReasoningModes.Fast, null, environment.RootPath, CancellationToken.None));
        Assert.Single(gateway.Transport.Requests);
        Assert.Equal("failed", Assert.Single(gateway.Completions).Status);
    }

    [Theory]
    [InlineData("appId: com.example.app\n---\n- tapOn: [broken")]
    [InlineData("appId: com.example.app\n---\n- runScript: anything.js")]
    [InlineData("appId: other.app\n---\n- tapOn: Continue")]
    [InlineData("appId: com.example.app\n---\n- launchApp")]
    [InlineData("appId: com.example.app\n---\n- tapOn: Continue\n  runScript: anything.js")]
    [InlineData("appId: com.example.app\nonFlowStart:\n  - runScript: anything.js\n---\n- tapOn: Continue")]
    public void MaestroValidationRejectsMalformedOrUnsupportedFlows(string source)
        => Assert.Throws<InvalidDataException>(() => ExternalScriptValidator.ValidateMaestro(source, AppId));

    [Fact]
    public async Task AppiumValidationParsesWithoutExecutingOrImportingDependencies()
    {
        await ExternalScriptValidator.ValidateAsync("appium",
            $"import {{ remote }} from 'webdriverio';\nconst appId = \"{AppId}\";\nthrow new Error('Must not run');\n",
            AppId, null, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => ExternalScriptValidator.ValidateAppiumTarget(
            $"// const appId = \"{AppId}\";\nconst appId = \"other.app\";", AppId));
    }

    private static AppSessionSnapshot CreateSnapshot() => new()
    {
        SessionId = "external-extraction", AppId = AppId, ClientName = "Example", RemoteAddress = "127.0.0.1",
        CreatedUtc = start, LastUpdatedUtc = start.AddSeconds(20), ConfigId = null, Status = "Closed", IsHistorical = true,
        MetricChannels = [], Metrics = [],
        Images = new[] { 1, 9, 15 }.Select(second => new SessionImageFrame
        {
            FrameId = $"frame-{second}", CapturedAtUtc = start.AddSeconds(second), Format = "png",
            Width = 4, Height = 4, Quality = 100, ByteCount = 1
        }).ToArray(),
        VisualTreeSnapshots = new[] { "first-control", "last-control", "outside-control" }.Select((id, index) => new SessionVisualTreeSnapshot
        {
            SnapshotId = id, CapturedAtUtc = start.AddSeconds(index == 0 ? 1 : index == 1 ? 9 : 15), Source = "test", NodeCount = 1,
            Payload = new JsonObject { ["root"] = new JsonObject { ["automationId"] = id, ["label"] = id, ["type"] = "Button" } }
        }).ToArray(),
        Annotations =
        [
            new SessionAnnotation { AnnotationId = "inside", StartUtc = start.AddSeconds(2), EndUtc = start.AddSeconds(8), Label = "Confirm checkout" },
            new SessionAnnotation { AnnotationId = "outside", StartUtc = start.AddSeconds(12), EndUtc = start.AddSeconds(15), Label = "Outside annotation" }
        ]
    };

    private static void WriteScreenshots(RuntimeCoordinator runtime, AppSessionSnapshot snapshot)
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        foreach (var frame in snapshot.Images)
        {
            var path = SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, snapshot, frame);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data.ToArray());
        }
    }

    private sealed class Gateway(params string[] sources) : IWorkspaceTestRunGateway
    {
        public Transport Transport { get; } = new(sources);
        public WorkspaceTestRunPreparationRequest? Request { get; private set; }
        public List<WorkspaceTestRunMeterCompletion> Completions { get; } = [];
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(WorkspaceTestRunPreparation.WithTransport(Transport, Guid.NewGuid(), null, Guid.NewGuid(), "Test"));
        }
        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            return Task.FromResult(WorkspaceTestRunMeterResult.Success());
        }
    }

    private sealed class Transport(string[] sources) : IModelExecutionTransport
    {
        public string Description => "Test transport";
        public bool SupportsWebSockets => false;
        public string AccessKey { get; set; } = "test-key";
        public bool CancelRequest { get; set; }
        public List<JsonObject> Requests { get; } = [];
        public ValueTask<string> ResolveAccessKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(AccessKey);
        public string DescribeFailure(int statusCode, string responseContent) => responseContent;
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
            if (CancelRequest) throw new OperationCanceledException();
            var source = sources[Math.Min(Requests.Count - 1, sources.Length - 1)];
            var response = new
            {
                id = $"response-{Requests.Count}", model = "test-model",
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = source } } } },
                usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }
    }
}
