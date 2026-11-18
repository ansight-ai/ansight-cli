using System.Text.Json;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAgentChatSource
{
    public async Task<RemoteAnnotationBatchSubmissionResult> SubmitAnnotationBatchAsync(
        RemoteAnnotationBatchSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var invalidRequest = request.Provider != "codex"
            || string.IsNullOrWhiteSpace(request.ProjectId)
            || string.IsNullOrWhiteSpace(request.BatchId)
            || request.AnnotationIds.Count is < 1 or > 100;

        if (invalidRequest)
        {
            return new RemoteAnnotationBatchSubmissionResult(false, "Choose a linked Codex project and a complete annotation set.", "failed");
        }

        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CompanionAgentReceipt? receipt = null;

        try
        {
            var session = await runtime.Sessions.LoadSnapshotAsync(request.SessionId,
                cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Ansight session is no longer available.");
            var evidence = PrepareEvidence(session, request);
            receipt = store.ReadReceipt(request.SessionId, request.BatchId);

            if (receipt is not null)
            {
                if (receipt.Fingerprint != evidence.Fingerprint)
                {
                    return new RemoteAnnotationBatchSubmissionResult(false,
                        "This annotation set was already submitted with different content or a different destination. Capture a new set for additional work.", "conflict");
                }

                var deliveryUncertain = receipt.Link.Status is "creating" or "dispatching" or "needs_attention";

                if (deliveryUncertain)
                {
                    receipt = SaveReceipt(receipt with
                    {
                        Link = receipt.Link with
                        {
                            Status = "needs_attention",
                            StatusMessage = "Delivery was interrupted. Check the linked Codex task before sending another annotation set.",
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        }
                    });
                }

                if (receipt.Link.Status is not ("prepared" or "failed"))
                {
                    return ToSubmission(receipt.Link);
                }
            }

            var projectLink = store.ReadProject(session.AppId)
                ?? throw new InvalidOperationException("Choose a Codex project for this app before sending annotations.");

            if (projectLink.ProjectId != request.ProjectId)
            {
                throw new InvalidOperationException("The app's linked project changed. Refresh and choose the destination again.");
            }

            await using var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var project = await ReadProjectAsync(connection, projectLink.ProjectId, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            receipt ??= new CompanionAgentReceipt(evidence.Fingerprint, new RemoteAgentTaskLink(
                Guid.NewGuid().ToString("N"), project.Id, session.SessionId, request.BatchId,
                request.AnnotationIds.ToArray(), [request.FrameId], "simulator.remote.mobile", "codex",
                request.StartNewTask ? string.Empty : request.ChatSessionId, null, request.ChatTitle,
                projectLink.WorkingDirectory, evidence.Prompt, "prepared", "Preparing annotation delivery.", now, now));

            JsonElement thread;

            if (request.StartNewTask && string.IsNullOrEmpty(receipt.Link.ProviderTaskId))
            {
                var hasCheckout = projectLink.WorkingDirectory is not null
                    && project.Roots.Contains(projectLink.WorkingDirectory, StringComparer.Ordinal)
                    && Directory.Exists(projectLink.WorkingDirectory);

                if (!hasCheckout)
                {
                    throw new InvalidOperationException("Choose an available project checkout before starting new work.");
                }

                receipt = SaveReceipt(receipt with { Link = receipt.Link with { Status = "creating" } });
                var created = await connection.CallAsync("thread/start", new
                {
                    projectId = project.Id,
                    cwd = projectLink.WorkingDirectory,
                    runtimeWorkspaceRoots = project.Roots,
                    ephemeral = false
                }, cancellationToken).ConfigureAwait(false);
                thread = created.GetProperty("thread");
                receipt = SaveReceipt(receipt with
                {
                    Link = receipt.Link with
                    {
                        ProviderTaskId = thread.GetProperty("id").GetString()!,
                        Status = "prepared"
                    }
                });
            }
            else
            {
                var read = await connection.CallAsync("thread/read", new
                {
                    threadId = receipt.Link.ProviderTaskId
                }, cancellationToken).ConfigureAwait(false);
                thread = read.GetProperty("thread");
            }

            if (!BelongsToProject(thread, project))
            {
                throw new InvalidOperationException("This Codex task no longer belongs to the app's linked project.");
            }

            var task = ParseThread(thread);
            receipt = SaveReceipt(receipt with
            {
                Link = receipt.Link with
                {
                    ProviderTaskId = task.SessionId,
                    ProviderTaskTitle = task.Title,
                    WorkingDirectory = task.WorkingDirectory,
                    Status = "prepared"
                }
            });

            if (task.Status == "notLoaded")
            {
                receipt = SaveReceipt(receipt with { Link = receipt.Link with { Status = "dispatching" } });
                await connections.QueueAsync(task.SessionId, evidence.Prompt, cancellationToken).ConfigureAwait(false);
                receipt = receipt with
                {
                    Link = receipt.Link with
                    {
                        Status = "queued",
                        StatusMessage = "Annotations queued in the original Codex task. Codex will continue it when available.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                };
                SaveReceipt(receipt);

                return ToSubmission(receipt.Link);
            }

            var input = new object[]
            {
                new { type = "text", text = evidence.Prompt },
                new { type = "localImage", path = evidence.ImagePath }
            };
            string? activeTurnId = null;

            if (task.Status == "active")
            {
                var turns = await connection.CallAsync("thread/turns/list", new
                {
                    threadId = task.SessionId,
                    limit = 1,
                    sortDirection = "desc",
                    itemsView = "notLoaded"
                }, cancellationToken).ConfigureAwait(false);
                activeTurnId = turns.GetProperty("data").EnumerateArray()
                    .Where(turn => ReadString(turn, "status") == "inProgress")
                    .Select(turn => ReadString(turn, "id"))
                    .FirstOrDefault();
            }

            receipt = SaveReceipt(receipt with { Link = receipt.Link with { Status = "dispatching" } });
            var response = activeTurnId is null
                ? await connection.CallAsync("turn/start", new
                {
                    threadId = task.SessionId,
                    input,
                    clientUserMessageId = receipt.Link.LinkId
                }, cancellationToken).ConfigureAwait(false)
                : await connection.CallAsync("turn/steer", new
                {
                    threadId = task.SessionId,
                    expectedTurnId = activeTurnId,
                    input,
                    clientUserMessageId = receipt.Link.LinkId
                }, cancellationToken).ConfigureAwait(false);
            var turnId = activeTurnId is null
                ? response.GetProperty("turn").GetProperty("id").GetString()
                : response.GetProperty("turnId").GetString();
            receipt = receipt with
            {
                Link = receipt.Link with
                {
                    ProviderTurnId = turnId,
                    Status = activeTurnId is null ? "accepted" : "steered",
                    StatusMessage = activeTurnId is null ? "Annotations accepted by Codex." : "Annotations delivered to the active Codex turn.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }
            };
            SaveReceipt(receipt);

            return ToSubmission(receipt.Link);
        }
        catch (Exception error)
        {
            if (receipt?.Link.Status is "accepted" or "steered" or "queued")
            {
                return ToSubmission(receipt.Link);
            }

            var mightHaveDelivered = receipt?.Link.Status is "creating" or "dispatching";

            if (receipt is not null)
            {
                var uncertain = mightHaveDelivered && error is not CodexRpcException;
                receipt = SaveReceipt(receipt with
                {
                    Link = receipt.Link with
                    {
                        Status = uncertain ? "needs_attention" : "failed",
                        StatusMessage = uncertain
                            ? "Codex may have received this annotation set. Check the linked task before sending more work."
                            : DescribeError(error),
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                });

                return ToSubmission(receipt.Link);
            }

            return new RemoteAnnotationBatchSubmissionResult(false, DescribeError(error), "failed");
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private CompanionAgentReceipt SaveReceipt(CompanionAgentReceipt receipt)
    {
        // Commit the delivery receipt before the session projection. A failed
        // projection must not cause an acknowledged turn to be sent a second time.
        store.SaveReceipt(receipt);

        if (!string.IsNullOrEmpty(receipt.Link.ProviderTaskId))
        {
            var link = receipt.Link;
            runtime.SessionEditing.UpsertAgentTaskLink(link.SessionId, new SessionAgentTaskLink
            {
                LinkId = link.LinkId,
                WorkContextId = link.WorkContextId,
                SessionId = link.SessionId,
                AnnotationBatchId = link.AnnotationBatchId,
                AnnotationIds = link.AnnotationIds,
                FrameIds = link.FrameIds,
                Source = link.Source,
                Provider = link.Provider,
                ProviderTaskId = link.ProviderTaskId,
                ProviderTurnId = link.ProviderTurnId,
                ProviderTaskTitle = link.ProviderTaskTitle,
                WorkingDirectory = link.WorkingDirectory,
                SubmittedPrompt = link.SubmittedPrompt,
                Status = link.Status,
                StatusMessage = link.StatusMessage,
                CreatedAtUtc = link.CreatedAtUtc,
                UpdatedAtUtc = link.UpdatedAtUtc
            });
        }

        return receipt;
    }

    private static RemoteAnnotationBatchSubmissionResult ToSubmission(RemoteAgentTaskLink link)
        => new(link.Status != "failed", link.StatusMessage ?? link.Status, link.Status,
            link.ProviderTurnId, link.ProviderTaskId, link.ProviderTaskTitle, link);
}
