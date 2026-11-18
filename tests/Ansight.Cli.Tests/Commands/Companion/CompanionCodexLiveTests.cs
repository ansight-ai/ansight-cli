using System.Text.Json;
using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionCodexLiveTests
{
    [CodexLiveFact]
    public async Task RealCodex_ProjectLinkNewTaskEvidenceAndDeliveryReceipt()
    {
        await using var context = await CompanionCodexTestContext.CreateAsync(
            "This is an integration test. Do not edit files or run tools. Look at the attached screenshot and reply only: Annotation received.");
        var factory = new CodexRpcConnectionFactory();
        await using var control = await factory.ConnectAsync(CancellationToken.None);
        var projectResponse = await control.CallAsync("project/create", new
        {
            name = "Ansight companion integration test",
            idempotencyKey = Guid.NewGuid().ToString(),
            roots = new[] { new { path = context.Path } }
        }, CancellationToken.None);
        var projectId = projectResponse.GetProperty("project").GetProperty("id").GetString()!;
        string? taskId = null;

        try
        {
            var source = new CliRemoteAgentChatSource(context.Runtime, factory);
            var linked = await source.UpdateProjectAsync(new RemoteAgentProjectUpdateRequest(context.Session.AppId, projectId, context.Path));
            Assert.True(linked.IsSuccess, linked.Message);
            var request = context.Submission(newTask: true) with { ProjectId = projectId };
            var submitted = await source.SubmitAnnotationBatchAsync(request);
            taskId = submitted.ProviderTaskId;
            Assert.True(submitted.IsSuccess, submitted.Message);
            Assert.Equal("accepted", submitted.Status);
            Assert.NotNull(taskId);

            var original = context.Session.Annotations[0];
            var steering = context.Runtime.SessionEditing.UpsertAnnotation(context.Session.SessionId, new SessionAnnotation
            {
                AnnotationId = "annotation-steer", CaptureGroupId = "batch-steer", StartUtc = original.StartUtc,
                Label = "Integration steering", Notes = "This is follow-up guidance for the same integration test. Reply only: Steering received. Do not edit files or run tools.",
                Geometry = original.Geometry
            });
            Assert.True(steering.IsSuccess, steering.Message);
            var steered = await source.SubmitAnnotationBatchAsync(request with
            {
                BatchId = "batch-steer", AnnotationIds = ["annotation-steer"],
                StartNewTask = false, ChatSessionId = taskId!
            });
            Assert.True(steered.IsSuccess, steered.Message);
            Assert.Equal("steered", steered.Status);
            Assert.Equal(submitted.ProviderTurnId, steered.ProviderTurnId);

            var tasks = await source.ListAgentChatsAsync(new RemoteAgentChatListRequest(context.Session.SessionId));
            Assert.True(tasks.IsSuccess, tasks.Message);
            Assert.Contains(tasks.Sessions, task => task.SessionId == taskId);
            var replay = await new CliRemoteAgentChatSource(context.Runtime, factory).SubmitAnnotationBatchAsync(request);
            Assert.Equal(submitted.ProviderTurnId, replay.ProviderTurnId);

            var read = await control.CallAsync("thread/read", new { threadId = taskId }, CancellationToken.None);
            Assert.Equal(projectId, read.GetProperty("thread").GetProperty("projectId").GetString());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            RemoteAgentTaskLink? completed = null;

            while (!timeout.IsCancellationRequested)
            {
                var history = await source.ListAgentTasksAsync(new RemoteAgentTaskListRequest(context.Session.SessionId), timeout.Token);
                completed = history.Tasks.FirstOrDefault(task => task.ProviderTaskId == taskId);

                if (completed?.Status is "ready_for_review" or "work_failed" or "interrupted")
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }

            Assert.Equal("ready_for_review", completed?.Status);

            // Exercise the installed CLI's acknowledgement format as well as
            // the WebSocket transport used by the shared server.
            await factory.QueueAsync(taskId!, "Integration test complete. Reply only: Queue received. Do not run tools or edit files.", CancellationToken.None);
        }
        finally
        {
            if (!string.IsNullOrEmpty(taskId))
            {
                var read = await control.CallAsync("thread/read", new { threadId = taskId }, CancellationToken.None);
                var state = read.GetProperty("thread").GetProperty("status").GetProperty("type").GetString();

                if (state == "active")
                {
                    var turns = await control.CallAsync("thread/turns/list", new { threadId = taskId, limit = 1 }, CancellationToken.None);
                    var turnId = turns.GetProperty("data")[0].GetProperty("id").GetString();
                    await control.CallAsync("turn/interrupt", new { threadId = taskId, turnId }, CancellationToken.None);
                }

                await control.CallAsync("thread/archive", new { threadId = taskId }, CancellationToken.None);
            }

            await control.CallAsync("project/delete", new { projectId }, CancellationToken.None);
        }
    }
}

internal sealed class CodexLiveFactAttribute : FactAttribute
{
    public CodexLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANSIGHT_RUN_CODEX_INTEGRATION") != "1")
        {
            Skip = "Set ANSIGHT_RUN_CODEX_INTEGRATION=1 to exercise the installed Codex server with a temporary project and task.";
        }
    }
}
