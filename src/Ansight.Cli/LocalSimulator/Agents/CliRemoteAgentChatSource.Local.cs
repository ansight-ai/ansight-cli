using System.Collections.Concurrent;
using System.Text.Json;
using Ansight.Host.Agents;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAgentChatSource
{
    private readonly CancellationTokenSource localAgentShutdown = new();
    private readonly ConcurrentDictionary<string, Task> localAgentTasks = new();
    private readonly SemaphoreSlim localSendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> claudeRuns = new();
    internal IClaudeAnnotationClient Claude { get; set; } = new ClaudeAnnotationClient();

    public async Task<LocalAgentCatalog> CatalogAsync(string sessionId, string provider, string? projectId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Session not found.");
            if (provider == "claude")
            {
                var root = runtime.Apps.Get(session.AppId)?.CodebasePath;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    throw new InvalidDataException("Set this app’s codebase folder in Apps before sending to Claude Code.");
                }

                var chats = await Claude.ListAsync(root, cancellationToken);
                return new(true, "Choose a Claude Code chat or start a new one.",
                    [new RemoteAgentProject(root, runtime.Apps.Get(session.AppId)?.Name ?? session.AppId, [root])], chats, root);
            }
            if (provider != "codex")
            {
                throw new InvalidDataException("Choose Codex or Claude Code.");
            }

            var catalog = await ListProjectsAsync(cancellationToken);
            if (!catalog.CodexAvailable)
            {
                return new(false, catalog.Message, [], []);
            }

            projectId ??= store.ReadProject(session.AppId)?.ProjectId
                ?? catalog.Projects.FirstOrDefault(p => p.Roots.Contains(runtime.Apps.Get(session.AppId)?.CodebasePath ?? ""))?.Id;
            var project = catalog.Projects.FirstOrDefault(p => p.Id == projectId);
            if (project is null)
            {
                return new(true, "Choose a Codex project.", catalog.Projects, [], CanQueue: true);
            }

            await using var connection = await connections.ConnectAsync(cancellationToken);
            var threads = new List<JsonElement>();
            var result = await connection.CallAsync("thread/list", new
            {
                projectId,
                limit = 100,
                sortKey = "recency_at",
                sortDirection = "desc",
                sourceKinds = new[] { "cli", "vscode", "appServer", "exec" },
                archived = false
            }, cancellationToken);
            threads.AddRange(result.GetProperty("data").EnumerateArray().Select(t => t.Clone()));
            if (project.Roots.Count > 0)
            {
                var legacy = await connection.CallAsync("thread/list", new
                {
                    projectId = (string?)null,
                    cwd = project.Roots,
                    limit = 100,
                    sortKey = "recency_at",
                    sortDirection = "desc",
                    sourceKinds = new[] { "cli", "vscode", "appServer", "exec" },
                    archived = false
                }, cancellationToken);
                threads.AddRange(legacy.GetProperty("data").EnumerateArray().Select(t => t.Clone()));
            }
            return new(true, "Choose a recent Codex chat or start a new one.", catalog.Projects,
                threads.Where(t => BelongsToProject(t, project)).Select(ParseThread).DistinctBy(t => t.SessionId)
                    .OrderByDescending(t => t.UpdatedAtUtc).Take(100).ToArray(), project.Id, CanQueue: true);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, error.Message, [], []);
        }
    }

    public async Task<RemoteAnnotationBatchSubmissionResult> SendAsync(string sessionId, LocalAnnotationSendRequest request, CancellationToken cancellationToken)
    {
        var annotationIds = (request.AnnotationIds ?? [request.AnnotationId]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!Guid.TryParse(request.SubmissionId, out _) || annotationIds.Length is < 1 or > 1000 || annotationIds.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(request.ProjectId) || request.Provider is not ("codex" or "claude")
            || (!request.StartNewTask && string.IsNullOrWhiteSpace(request.ChatSessionId)))
        {
            return new(false, "Choose a provider, project and chat before sending.", "failed");
        }

        if (request.QueueAfterCurrent && (request.Provider != "codex" || request.StartNewTask))
        {
            return new(false, "Queueing requires an existing Codex chat.", "failed");
        }

        await localSendGate.WaitAsync(cancellationToken);
        try
        {
            var session = await runtime.Sessions.LoadSnapshotAsync(sessionId, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Session not found.");
            var selectedAnnotations = annotationIds.Select(id => session.Annotations.FirstOrDefault(a => a.AnnotationId == id)
                ?? throw new InvalidDataException("Save every comment before sending the recording.")).OrderBy(a => a.StartUtc).ThenBy(a => a.AnnotationId, StringComparer.Ordinal).ToArray();
            annotationIds = selectedAnnotations.Select(a => a.AnnotationId).ToArray();
            var annotation = selectedAnnotations.FirstOrDefault(a => a.Geometry.Count > 0) ?? selectedAnnotations[0];
            var frameId = annotation.Geometry.FirstOrDefault()?.FrameId
                ?? session.Images.OrderBy(frame => Math.Abs((frame.CapturedAtUtc - annotation.StartUtc).TotalMilliseconds))
                    .FirstOrDefault()?.FrameId ?? "";
            var submission = new RemoteAnnotationBatchSubmissionRequest("local-" + request.SubmissionId, sessionId, frameId,
                annotationIds, request.Provider, request.StartNewTask ? "" : request.ChatSessionId!,
                ProjectId: request.ProjectId, StartNewTask: request.StartNewTask, QueueAfterCurrent: request.QueueAfterCurrent);
            if (request.Provider == "codex")
            {
                return await SubmitAnnotationBatchCoreAsync(submission, true, cancellationToken);
            }

            var evidence = PrepareEvidence(session, submission, true);
            var prior = store.ReadReceipt(sessionId, submission.BatchId);
            if (prior is not null)
            {
                if (prior.Fingerprint != evidence.Fingerprint)
                {
                    return new(false, "This delivery already contains different content. Start a new delivery to send the edited annotation.", "conflict");
                }

                return ToSubmission(prior.Link);
            }
            var root = runtime.Apps.Get(session.AppId)?.CodebasePath;
            if (string.IsNullOrWhiteSpace(root) || request.ProjectId != root || !Directory.Exists(root))
            {
                throw new InvalidDataException("The app’s codebase folder changed. Refresh the destination.");
            }

            var chats = await Claude.ListAsync(root, cancellationToken);
            var chat = chats.FirstOrDefault(c => c.SessionId == request.ChatSessionId);
            if (!request.StartNewTask && chat is null)
            {
                throw new InvalidDataException("This Claude chat does not belong to the app’s codebase.");
            }

            var threadId = request.StartNewTask ? Guid.NewGuid().ToString() : chat!.SessionId;
            if (!claudeRuns.TryAdd(threadId, 0))
            {
                throw new InvalidDataException("Claude is already processing an annotation in this chat. Wait for it to finish.");
            }

            var now = DateTimeOffset.UtcNow;
            var receipt = new CompanionAgentReceipt(evidence.Fingerprint, new RemoteAgentTaskLink(
                Guid.NewGuid().ToString("N"), root, sessionId, submission.BatchId, annotationIds,
                string.IsNullOrEmpty(frameId) ? [] : [frameId], "localPortal", "claude", threadId, null,
                chat?.Title ?? "Review app annotation", root, evidence.Prompt, "starting",
                "Starting Claude Code. Delivery has not yet been acknowledged.", now, now));
            try
            { SaveReceipt(receipt); }
            catch { claudeRuns.TryRemove(threadId, out _); throw; }
            var task = RunClaudeAsync(receipt, evidence, request.StartNewTask);
            localAgentTasks[receipt.Link.LinkId] = task;
            if (task.IsCompleted)
                localAgentTasks.TryRemove(receipt.Link.LinkId, out _);
            return ToSubmission(receipt.Link);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, error.Message, "failed");
        }
        finally { localSendGate.Release(); }
    }

    private async Task RunClaudeAsync(CompanionAgentReceipt receipt, CompanionAnnotationEvidence evidence, bool newChat)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(runtime.FeatureLifetime, localAgentShutdown.Token);
        try
        {
            await Claude.SendAsync(receipt.Link.WorkingDirectory!, receipt.Link.ProviderTaskId, newChat, evidence,
                (status, message) =>
                {
                    receipt = SaveReceipt(receipt with
                    {
                        Link = receipt.Link with
                        { Status = status, StatusMessage = message, UpdatedAtUtc = DateTimeOffset.UtcNow }
                    });
                }, lifetime.Token);
        }
        catch (Exception error)
        {
            try
            {
                SaveReceipt(receipt with
                {
                    Link = receipt.Link with
                    {
                        Status = "needs_attention",
                        StatusMessage = "Claude delivery stopped: " + error.Message + " Check the chat before starting another delivery.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                });
            }
            catch { /* Preserve the last durable receipt; status recovery marks it uncertain. */ }
        }
        finally
        {
            claudeRuns.TryRemove(receipt.Link.ProviderTaskId, out _);
            localAgentTasks.TryRemove(receipt.Link.LinkId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await localAgentShutdown.CancelAsync();
        await Task.WhenAll(localAgentTasks.Values.ToArray());
        localAgentShutdown.Dispose();
    }

    public async Task<RemoteAnnotationBatchSubmissionResult> StatusAsync(string sessionId, string submissionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(submissionId, out _))
        {
            return new(false, "Invalid delivery ID.", "failed");
        }

        var receipt = store.ReadReceipt(sessionId, "local-" + submissionId);
        if (receipt is null)
        {
            return new(false, "No saved delivery was found. The annotation has not been sent.", "not_found");
        }

        if (receipt.Link.Provider == "codex")
        {
            try
            { await RefreshDeliveryStatusesAsync(sessionId, cancellationToken); }
            catch { /* Keep the acknowledged receipt when Codex is offline. */ }
            receipt = store.ReadReceipt(sessionId, "local-" + submissionId)!;
        }
        else if (receipt.Link.Status is "starting" or "running" && !claudeRuns.ContainsKey(receipt.Link.ProviderTaskId))
        {
            // Re-read after checking the worker: it may just have committed a final result.
            // Do not overwrite a concurrent completion while recovering an interrupted host.
            receipt = store.ReadReceipt(sessionId, "local-" + submissionId)!;
            if (receipt.Link.Status is "starting" or "running")
            {
                return ToSubmission(receipt.Link with
                {
                    Status = "needs_attention",
                    StatusMessage = "The host restarted during delivery. Check the Claude chat before starting another delivery."
                });
            }
        }
        return ToSubmission(receipt.Link);
    }
}
