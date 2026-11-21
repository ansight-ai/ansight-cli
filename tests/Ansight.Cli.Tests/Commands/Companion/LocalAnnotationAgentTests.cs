using System.Text.Json;
using System.Net.Http.Json;
using Ansight.Host.Agents;
using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class LocalAnnotationAgentTests
{
    private static LocalAnnotationSendRequest Request(CompanionCodexTestContext context, string provider = "codex", bool newChat = false)
        => new(Guid.NewGuid().ToString(), "annotation-1", provider, provider == "codex" ? "project-1" : context.Path,
            newChat ? null : provider == "codex" ? "thread-1" : "a4951880-7135-4d65-ac36-e16c5ab55518", newChat);

    [Theory]
    [InlineData("active")]
    [InlineData("idle")]
    [InlineData("notLoaded")]
    public async Task QueueUsesChatQueueWithoutStartingOrSteeringAndDeduplicates(string status)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        context.Codex.Status = status;
        var request = Request(context) with { QueueAfterCurrent = true };
        var first = await context.Source.SendAsync(context.Session.SessionId, request, default);
        var retry = await context.Source.SendAsync(context.Session.SessionId, request, default);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal("queued", first.Status);
        Assert.Equal(first.TaskLink?.LinkId, retry.TaskLink?.LinkId);
        var queued = Assert.Single(context.Codex.Calls, c => c.Method == "queue");
        Assert.Contains("Keep the button visible", queued.Parameters.GetProperty("prompt").GetString());
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method is "turn/start" or "turn/steer" or "thread/start");
    }

    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", true)]
    public async Task QueueRejectsUnsupportedDestinations(string provider, bool newChat)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var result = await context.Source.SendAsync(context.Session.SessionId,
            Request(context, provider, newChat) with { QueueAfterCurrent = true }, default);
        Assert.False(result.IsSuccess);
        Assert.Empty(context.Codex.Calls);
    }

    [Fact]
    public async Task QueueEndpointForcesQueueEvenWithoutFlagAndAdvertisesCapability()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        context.Runtime.LocalAnnotationAgents = context.Source;
        context.Codex.Status = "active";
        var explorer = await context.Runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = explorer.ExplorerUrl };
        var route = "api/sessions/" + context.Session.SessionId + "/annotation-agents/";
        var catalog = await client.GetFromJsonAsync<LocalAgentCatalog>(route + "catalog?provider=codex");
        Assert.True(catalog!.CanQueue);
        var response = await client.PostAsJsonAsync(route + "queue", Request(context));
        response.EnsureSuccessStatusCode();
        var sent = await response.Content.ReadFromJsonAsync<Ansight.RemoteSimulator.Core.Annotations.RemoteAnnotationBatchSubmissionResult>();
        Assert.Equal("queued", sent!.Status);
        Assert.Single(context.Codex.Calls, c => c.Method == "queue");
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method is "turn/start" or "turn/steer");
    }

    [Fact]
    public async Task LocalCatalogFiltersForeignChatsWithoutRequiringCompanionLink()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var result = await context.Source.CatalogAsync(context.Session.SessionId, "codex", null, default);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("project-1", result.ProjectId);
        Assert.Equal("thread-1", Assert.Single(result.Chats).SessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalSendsSavedAnnotationAndDeduplicatesRetries(bool newChat)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var request = Request(context, newChat: newChat);
        var first = await context.Source.SendAsync(context.Session.SessionId, request, default);
        var second = await context.Source.SendAsync(context.Session.SessionId, request, default);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal("accepted", first.Status);
        Assert.Equal(first.TaskLink?.LinkId, second.TaskLink?.LinkId);
        var sent = Assert.Single(context.Codex.Calls, c => c.Method == "turn/start");
        var prompt = sent.Parameters.GetProperty("input")[0].GetProperty("text").GetString();
        Assert.Contains("Keep the button visible", prompt);
        Assert.Contains("Keep this button visible when the keyboard opens.", prompt);
        Assert.Contains("Ansight Local", prompt);
        Assert.Equal("localPortal", first.TaskLink?.Source);
        Assert.Equal(2, sent.Parameters.GetProperty("input").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalRecordingSendsAllCommentsOnceInTimeOrder(bool queue)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var saved = context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, new SessionAnnotation
        {
            AnnotationId = "speech-2", StartUtc = context.Session.CreatedUtc.AddSeconds(10), EndUtc = context.Session.CreatedUtc.AddSeconds(12),
            Label = "Move the filter lower", Source = "localPortal.exploration", CaptureGroupId = "recording-1"
        });
        Assert.True(saved.IsSuccess, saved.Message);
        var request = Request(context) with { AnnotationIds = ["speech-2", "annotation-1", "speech-2"], QueueAfterCurrent = queue };
        var first = await context.Source.SendAsync(context.Session.SessionId, request, default);
        var repeated = await context.Source.SendAsync(context.Session.SessionId, request with { AnnotationIds = ["annotation-1", "speech-2"] }, default);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(first.TaskLink?.LinkId, repeated.TaskLink?.LinkId);
        var prompt = first.TaskLink!.SubmittedPrompt;
        Assert.Contains("Keep the button visible", prompt);
        Assert.Contains("Move the filter lower", prompt);
        Assert.Contains("Recording ID: recording-1", prompt);
        Assert.Contains("share a timeline", prompt);
        Assert.Equal(2, first.TaskLink.AnnotationIds.Count);
        Assert.Single(context.Codex.Calls, c => c.Method == (queue ? "queue" : "turn/start"));
    }

    [Fact]
    public async Task MissingBatchMemberRejectsWholeDelivery()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var result = await context.Source.SendAsync(context.Session.SessionId, Request(context) with { AnnotationIds = ["annotation-1", "missing"] }, default);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method is "turn/start" or "queue");
    }

    [Fact]
    public async Task LocalTimelineAnnotationDoesNotRequireScreenshotGeometry()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var saved = context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, new SessionAnnotation
        {
            AnnotationId = "timeline", StartUtc = context.Session.CreatedUtc, EndUtc = context.Session.CreatedUtc.AddSeconds(5), Label = "Inspect this time range"
        });
        Assert.True(saved.IsSuccess, saved.Message);
        var result = await context.Source.SendAsync(context.Session.SessionId, Request(context) with { AnnotationId = "timeline" }, default);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains("Inspect this time range", result.TaskLink?.SubmittedPrompt);
    }

    [Fact]
    public async Task ForeignChatAndUnknownAnnotationCannotDispatch()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        context.Codex.ThreadProjectId = "other-project";
        var result = await context.Source.SendAsync(context.Session.SessionId, Request(context), default);
        Assert.False(result.IsSuccess);
        result = await context.Source.SendAsync(context.Session.SessionId, Request(context) with { AnnotationId = "missing" }, default);
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method == "turn/start" || c.Method == "queue");
    }

    [Fact]
    public async Task UnknownDeliveryDoesNotResendAfterTimeout()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        context.Codex.DeliveryError = new IOException("Disconnected after dispatch");
        var request = Request(context);
        var result = await context.Source.SendAsync(context.Session.SessionId, request, default);
        Assert.Equal("needs_attention", result.Status);
        await context.Source.SendAsync(context.Session.SessionId, request, default);
        Assert.Single(context.Codex.Calls, c => c.Method == "turn/start");
    }

    [Fact]
    public async Task ClaudeReceiptPersistsAcknowledgementAndDoesNotDispatchTwice()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var client = new TestClaudeClient(context.Path);
        context.Source.Claude = client;
        var request = Request(context, "claude");
        await context.Source.SendAsync(context.Session.SessionId, request, default);
        await context.Source.SendAsync(context.Session.SessionId, request, default);
        var result = await context.Source.StatusAsync(context.Session.SessionId, request.SubmissionId, default);
        Assert.Equal(1, client.Sends);
        Assert.Equal("ready_for_review", result.Status);
        Assert.Equal("claude", result.TaskLink?.Provider);
        Assert.Empty(context.Codex.Calls);
    }

    [Fact]
    public async Task ClaudeRejectsChatFromAnotherDirectory()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var client = new TestClaudeClient(context.Path);
        context.Source.Claude = client;
        var result = await context.Source.SendAsync(context.Session.SessionId,
            Request(context, "claude") with { ChatSessionId = Guid.NewGuid().ToString() }, default);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, client.Sends);
    }

    [Fact]
    public void ClaudeTranscriptDiscoveryChecksWorkspaceAndIgnoresPartialLines()
    {
        using var directory = TestDirectory.Create();
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(directory.Path, id + ".jsonl"),
            JsonSerializer.Serialize(new { type = "user", cwd = directory.Path, message = new { content = "Fix keyboard layout" } }) + "\n" +
            JsonSerializer.Serialize(new { type = "custom-title", customTitle = "Layout review" }) + "\n{unfinished");
        File.WriteAllText(Path.Combine(directory.Path, Guid.NewGuid() + ".jsonl"),
            JsonSerializer.Serialize(new { type = "user", cwd = "/another-project", message = new { content = "Other work" } }));
        var chat = Assert.Single(ClaudeAnnotationClient.ReadChats(directory.Path, directory.Path, default));
        Assert.Equal(id, chat.SessionId);
        Assert.Equal("Layout review", chat.Title);
    }

    [Theory]
    [InlineData(false, "--resume")]
    [InlineData(true, "--session-id")]
    public void ClaudeArgumentsPreservePermissionsAndDoNotInvokeShell(bool newChat, string expected)
    {
        var info = ClaudeAnnotationClient.CreateStartInfo("claude", "/tmp/work tree", Guid.NewGuid().ToString(), newChat);
        Assert.Contains(expected, info.ArgumentList);
        Assert.Contains("--replay-user-messages", info.ArgumentList);
        Assert.DoesNotContain("--dangerously-skip-permissions", info.ArgumentList);
        Assert.DoesNotContain("--permission-mode", info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
    }

    [Fact]
    public async Task LocalExplorerRoutesCatalogSendAndStatusAndRejectsForeignOrigins()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        context.Runtime.LocalAnnotationAgents = context.Source;
        var explorer = await context.Runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient { BaseAddress = explorer.ExplorerUrl };
        var route = "api/sessions/" + context.Session.SessionId + "/annotation-agents/";
        var catalog = await client.GetFromJsonAsync<LocalAgentCatalog>(route + "catalog?provider=codex");
        Assert.True(catalog!.IsSuccess, catalog.Message);
        var request = Request(context);
        using var rejected = new HttpRequestMessage(HttpMethod.Post, route + "send") { Content = JsonContent.Create(request) };
        rejected.Headers.Add("Origin", "https://unrelated.example");
        var denied = await client.SendAsync(rejected);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.DoesNotContain(context.Codex.Calls, c => c.Method == "turn/start");
        var response = await client.PostAsJsonAsync(route + "send", request);
        response.EnsureSuccessStatusCode();
        var sent = await response.Content.ReadFromJsonAsync<Ansight.RemoteSimulator.Core.Annotations.RemoteAnnotationBatchSubmissionResult>();
        Assert.Equal("accepted", sent!.Status);
        var status = await client.GetFromJsonAsync<Ansight.RemoteSimulator.Core.Annotations.RemoteAnnotationBatchSubmissionResult>(route + "status?submissionId=" + request.SubmissionId);
        Assert.Equal(sent.TaskLink!.LinkId, status!.TaskLink!.LinkId);
    }

    [Theory]
    [InlineData(false, "ready_for_review")]
    [InlineData(true, "needs_attention")]
    public async Task ClaudeProcessAcknowledgesInputAndReportsPermissionDenials(bool denied, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = TestDirectory.Create();
        var script = Path.Combine(directory.Path, "claude-test");
        var result = JsonSerializer.Serialize(new { type = "result", is_error = false, permission_denials = denied ? new[] { "Edit" } : Array.Empty<string>() });
        await File.WriteAllTextAsync(script, "#!/bin/sh\nIFS= read -r input\nprintf '%s\\n' \"$input\"\nprintf '%s\\n' '" + result + "'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statuses = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new ClaudeAnnotationClient(script).SendAsync(directory.Path, Guid.NewGuid().ToString(), false,
            new CompanionAnnotationEvidence("User’s annotation \"quoted\"\n第二行", null, "fingerprint"),
            (status, _) => statuses.Add(status), timeout.Token);
        Assert.Equal(new[] { "running", expected }, statuses);
    }

    private sealed class TestClaudeClient(string root) : IClaudeAnnotationClient
    {
        public int Sends { get; private set; }
        public Task<IReadOnlyList<RemoteAgentChatSession>> ListAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteAgentChatSession>>([new("claude", "a4951880-7135-4d65-ac36-e16c5ab55518", "Test chat", root, DateTimeOffset.UtcNow, "idle")]);
        public Task SendAsync(string path, string threadId, bool newChat, CompanionAnnotationEvidence evidence, Action<string, string> update, CancellationToken cancellationToken)
        {
            Sends++;
            update("running", "Acknowledged");
            update("ready_for_review", "Done");
            return Task.CompletedTask;
        }
    }
}
