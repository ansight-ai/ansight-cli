using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionCodexTests
{
    [Theory]
    [InlineData("creating", "")]
    [InlineData("dispatching", "thread-1")]
    public async Task RestartDuringDelivery_ShowsUncertaintyEvenWhenCodexIsOffline(string status, string taskId)
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var request = context.Submission();
        await context.Source.SubmitAnnotationBatchAsync(request);
        var store = new CompanionAgentStore(context.Runtime.BaseFolderPath);
        var receipt = Assert.IsType<CompanionAgentReceipt>(store.ReadReceipt(request.SessionId, request.BatchId));
        store.SaveReceipt(receipt with
        {
            Link = receipt.Link with
            {
                Status = status,
                ProviderTaskId = taskId,
                ProviderTurnId = null
            }
        });

        context.Codex.ConnectionError = new IOException("Codex offline");
        var restarted = new CliRemoteAgentChatSource(context.Runtime, context.Codex);
        var history = await restarted.ListAgentTasksAsync(new RemoteAgentTaskListRequest());

        Assert.Equal("needs_attention", Assert.Single(history.Tasks).Status);
        Assert.Equal("needs_attention", store.ReadReceipt(request.SessionId, request.BatchId)?.Link.Status);
        Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");
    }

    [Fact]
    public async Task UnassignedDesktopTasks_RequireExactProjectRootAndUseCodexQueue()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Codex.ThreadProjectId = null;
        context.Codex.WorkingDirectory = context.Path;
        context.Codex.Status = "notLoaded";
        var chats = await context.Source.ListAgentChatsAsync(new RemoteAgentChatListRequest(context.Session.SessionId));
        Assert.Single(chats.Sessions);
        var submission = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.True(submission.IsSuccess, submission.Message);
        Assert.Equal("queued", submission.Status);
        var queued = Assert.Single(context.Codex.Calls, call => call.Method == "queue");
        Assert.Contains("Marked screenshot:", queued.Parameters.GetProperty("prompt").GetString());
        Assert.DoesNotContain(context.Codex.Calls, call => call.Method is "thread/resume" or "turn/start");

        context.Codex.WorkingDirectory = context.Path + "-unrelated";
        var unrelated = await context.Source.ListAgentChatsAsync(new RemoteAgentChatListRequest(context.Session.SessionId));
        Assert.Empty(unrelated.Sessions);
    }

    [Fact]
    public async Task CompletedWork_UpdatesHistoryWithoutResubmittingTheAnnotationSet()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var delivered = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.True(delivered.IsSuccess, delivered.Message);
        context.Codex.TurnStatus = "completed";
        var history = await context.Source.ListAgentTasksAsync(new RemoteAgentTaskListRequest());
        Assert.Equal("ready_for_review", Assert.Single(history.Tasks).Status);
        var repeated = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.Equal("ready_for_review", repeated.Status);
        Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");
    }

    [Fact]
    public async Task ProjectLink_PersistsPerAppAndCanBeUnlinkedWhileCodexIsOffline()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Runtime.Apps.Register(new AppRegistrationRequest("other.app", "Other app"));
        var restarted = new CliRemoteAgentChatSource(context.Runtime, context.Codex);
        var catalog = await restarted.ListProjectsAsync();

        Assert.Equal("project-1", catalog.Apps.Single(app => app.AppId == context.Session.AppId).Project?.ProjectId);
        Assert.Null(catalog.Apps.Single(app => app.AppId == "other.app").Project);
        Assert.Equal(context.Path, context.Runtime.Apps.Get(context.Session.AppId)?.CodebasePath);

        context.Codex.ConnectionError = new IOException("Codex offline");
        var offline = await restarted.ListProjectsAsync();
        Assert.False(offline.CodexAvailable);
        Assert.NotNull(offline.Apps.Single(app => app.AppId == context.Session.AppId).Project);
        var unlinked = await restarted.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest(context.Session.AppId, null));
        Assert.True(unlinked.IsSuccess, unlinked.Message);
        var chats = await restarted.ListAgentChatsAsync(new RemoteAgentChatListRequest(context.Session.SessionId));
        Assert.True(chats.IsSuccess);
        Assert.Empty(chats.Sessions);
        Assert.Null(chats.Project);
    }

    [Fact]
    public async Task ProjectLink_RejectsUnknownAppsProjectsAndUnrelatedCheckouts()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        var unknownApp = await context.Source.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest("missing", "project-1"));
        var unknownProject = await context.Source.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest(context.Session.AppId, "missing"));
        var wrongRoot = await context.Source.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest(context.Session.AppId, "project-1", "/other"));
        Assert.False(unknownApp.IsSuccess);
        Assert.False(unknownProject.IsSuccess);
        Assert.False(wrongRoot.IsSuccess);
    }

    [Fact]
    public async Task RecentTasks_UseProjectMembershipAndKeepWorktreeAndActiveTasks()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Codex.Status = "active";
        var result = await context.Source.ListAgentChatsAsync(new RemoteAgentChatListRequest(context.Session.SessionId, 12));
        Assert.True(result.IsSuccess, result.Message);
        var task = Assert.Single(result.Sessions);
        Assert.Equal("thread-1", task.SessionId);
        Assert.EndsWith("/worktree", task.WorkingDirectory);
        Assert.Equal("fix-keyboard", task.Branch);
        Assert.Equal("active", task.Status);
        var query = context.Codex.Calls.First(call => call.Method == "thread/list").Parameters;
        Assert.Equal("project-1", query.GetProperty("projectId").GetString());
        Assert.Equal("recency_at", query.GetProperty("sortKey").GetString());
        Assert.Equal(12, query.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ExistingTask_ReceivesScreenshotAndNotesWithoutChangingItsPermissions()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var result = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("accepted", result.Status);
        Assert.DoesNotContain(context.Codex.Calls, call => call.Method == "thread/resume");
        var call = Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");
        Assert.Equal("thread-1", call.Parameters.GetProperty("threadId").GetString());
        Assert.False(call.Parameters.TryGetProperty("approvalPolicy", out _));
        Assert.False(call.Parameters.TryGetProperty("sandboxPolicy", out _));
        var input = call.Parameters.GetProperty("input");
        Assert.Contains("Keep this button visible", input[0].GetProperty("text").GetString());
        Assert.Equal("localImage", input[1].GetProperty("type").GetString());
        Assert.True(File.Exists(input[1].GetProperty("path").GetString()));
        var history = await context.Source.ListAgentTasksAsync(new RemoteAgentTaskListRequest());
        Assert.Equal("new-turn", Assert.Single(history.Tasks).ProviderTurnId);
    }

    [Fact]
    public async Task ActiveTask_IsSteeredUsingTheObservedTurnId()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Codex.Status = "active";
        var result = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("steered", result.Status);
        var call = Assert.Single(context.Codex.Calls, call => call.Method == "turn/steer");
        Assert.Equal("active-turn", call.Parameters.GetProperty("expectedTurnId").GetString());
        Assert.DoesNotContain(context.Codex.Calls, call => call.Method is "thread/resume" or "turn/start");
    }

    [Fact]
    public async Task NewTask_UsesLinkedProjectAndCheckoutAndSurvivesDuplicateSubmissions()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var request = context.Submission(newTask: true);
        var results = await Task.WhenAll(context.Source.SubmitAnnotationBatchAsync(request), context.Source.SubmitAnnotationBatchAsync(request));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        Assert.Equal(results[0].ProviderTaskId, results[1].ProviderTaskId);
        var created = Assert.Single(context.Codex.Calls, call => call.Method == "thread/start");
        Assert.Equal("project-1", created.Parameters.GetProperty("projectId").GetString());
        Assert.Equal(context.Path, created.Parameters.GetProperty("cwd").GetString());
        Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");

        var restarted = new CliRemoteAgentChatSource(context.Runtime, context.Codex);
        var replayed = await restarted.SubmitAnnotationBatchAsync(request);
        Assert.Equal(results[0].ProviderTurnId, replayed.ProviderTurnId);
        Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");
    }

    [Fact]
    public async Task LostAcknowledgement_IsRetainedWithoutDeliveringTwice()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Codex.DeliveryError = new IOException("Connection lost after writing the request");
        var first = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.Equal("needs_attention", first.Status);
        context.Codex.DeliveryError = null;
        var restarted = new CliRemoteAgentChatSource(context.Runtime, context.Codex);
        var second = await restarted.SubmitAnnotationBatchAsync(context.Submission());
        Assert.Equal("needs_attention", second.Status);
        Assert.Single(context.Codex.Calls, call => call.Method == "turn/start");
    }

    [Fact]
    public async Task RejectedDelivery_CanRetryWithoutCreatingAnotherTask()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        context.Codex.DeliveryError = new CodexRpcException("Turn changed; try again");
        var first = await context.Source.SubmitAnnotationBatchAsync(context.Submission(newTask: true));
        Assert.False(first.IsSuccess);
        context.Codex.DeliveryError = null;
        var second = await context.Source.SubmitAnnotationBatchAsync(context.Submission(newTask: true));
        Assert.True(second.IsSuccess, second.Message);
        Assert.Single(context.Codex.Calls, call => call.Method == "thread/start");
    }

    [Fact]
    public async Task Submission_RejectsStaleProjectAndForeignTasksAndAnnotations()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync();
        await context.LinkAsync();
        var stale = await context.Source.SubmitAnnotationBatchAsync(context.Submission() with { ProjectId = "other" });
        Assert.False(stale.IsSuccess);
        var wrongAnnotation = await context.Source.SubmitAnnotationBatchAsync(context.Submission() with { AnnotationIds = ["missing"] });
        Assert.False(wrongAnnotation.IsSuccess);
        context.Codex.ThreadProjectId = "other";
        var wrongTask = await context.Source.SubmitAnnotationBatchAsync(context.Submission());
        Assert.False(wrongTask.IsSuccess);
        Assert.DoesNotContain(context.Codex.Calls, call => call.Method is "turn/start" or "turn/steer" or "thread/start");
    }
}
