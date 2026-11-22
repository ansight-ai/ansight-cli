using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using System.Text.Json;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAgentChatSource : IRemoteAgentChatSource, Ansight.Host.Agents.ILocalAnnotationAgentSource
{
    private readonly RuntimeCoordinator runtime;
    private readonly ICodexRpcConnectionFactory connections;
    private readonly CompanionAgentStore store;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public CliRemoteAgentChatSource(RuntimeCoordinator runtime, ICodexRpcConnectionFactory? connections = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.connections = connections ?? new CodexRpcConnectionFactory();
        store = new CompanionAgentStore(runtime.BaseFolderPath);
    }

    public bool SupportsSubmission => true;
    public bool SupportsProjects => true;

    public async Task<RemoteAgentChatListResult> ListAgentChatsAsync(
        RemoteAgentChatListRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await runtime.Sessions.LoadSnapshotAsync(request.SessionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return new RemoteAgentChatListResult(false, "The Ansight session is no longer available.", []);
        }

        try
        {
            var project = store.ReadProject(session.AppId);

            if (project is null)
            {
                return new RemoteAgentChatListResult(true, "Choose a Codex project for this app.", [], session.AppId,
                    SupportsRecordingHandoff: true);
            }

            await using var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var definition = await ReadProjectAsync(connection, project.ProjectId, cancellationToken).ConfigureAwait(false);
            var result = await connection.CallAsync("thread/list", new
            {
                projectId = project.ProjectId,
                limit = Math.Clamp(request.Limit, 1, 100),
                sortKey = "recency_at",
                sortDirection = "desc",
                sourceKinds = new[] { "cli", "vscode", "appServer", "exec" },
                archived = false
            }, cancellationToken).ConfigureAwait(false);
            var threads = result.GetProperty("data").EnumerateArray().Select(static thread => thread.Clone()).ToList();

            if (definition.Roots.Count > 0)
            {
                // Older desktop chats predate native project assignment. Include
                // only unassigned chats in exact project roots, never a chat
                // explicitly assigned to another project.
                var legacy = await connection.CallAsync("thread/list", new
                {
                    projectId = (string?)null,
                    cwd = definition.Roots,
                    limit = Math.Clamp(request.Limit, 1, 100),
                    sortKey = "recency_at",
                    sortDirection = "desc",
                    sourceKinds = new[] { "cli", "vscode", "appServer", "exec" },
                    archived = false
                }, cancellationToken).ConfigureAwait(false);
                threads.AddRange(legacy.GetProperty("data").EnumerateArray().Select(static thread => thread.Clone()));
            }

            var recentlySubmitted = store.ReadReceipts().Where(receipt =>
            {
                var belongsToSession = receipt.Link.SessionId == session.SessionId
                    && receipt.Link.WorkContextId == project.ProjectId;
                var awaitsIndexing = receipt.Link.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-2)
                    && !string.IsNullOrEmpty(receipt.Link.ProviderTaskId);

                return belongsToSession && awaitsIndexing;
            });

            foreach (var receipt in recentlySubmitted)
            {
                if (threads.Any(thread => ReadString(thread, "id") == receipt.Link.ProviderTaskId))
                {
                    continue;
                }

                var read = await connection.CallAsync("thread/read", new { threadId = receipt.Link.ProviderTaskId },
                    cancellationToken).ConfigureAwait(false);
                threads.Add(read.GetProperty("thread").Clone());
            }

            var linkedTaskIds = session.AgentTaskLinks
                .Where(link => link.Provider == "codex" && link.WorkContextId == project.ProjectId)
                .OrderByDescending(static link => link.UpdatedAtUtc)
                .Select(link => link.ProviderTaskId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var latestLinkedTaskId = linkedTaskIds.FirstOrDefault();
            var preferredCandidates = new[] { latestLinkedTaskId, request.RememberedTaskId }
                .OfType<string>()
                .Where(id => id.Length is > 0 and <= 200)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var taskId in preferredCandidates)
            {
                if (threads.Any(thread => ReadString(thread, "id") == taskId))
                {
                    continue;
                }

                try
                {
                    var read = await connection.CallAsync("thread/read", new { threadId = taskId }, cancellationToken)
                        .ConfigureAwait(false);
                    var thread = read.GetProperty("thread");
                    if (!thread.TryGetProperty("archived", out var archived) || archived.ValueKind != JsonValueKind.True)
                    {
                        threads.Add(thread.Clone());
                    }
                }
                catch (CodexRpcException)
                {
                    // A previously selected task may have been removed.
                }
            }

            var tasks = threads
                .Where(thread => BelongsToProject(thread, definition))
                .Select(ParseThread)
                .DistinctBy(static task => task.SessionId)
                .OrderByDescending(task => task.SessionId == latestLinkedTaskId)
                .ThenByDescending(task => task.SessionId == request.RememberedTaskId)
                .ThenByDescending(static task => task.UpdatedAtUtc)
                .Take(Math.Clamp(request.Limit, 1, 100))
                .OrderByDescending(static task => task.UpdatedAtUtc)
                .ToArray();
            var preferred = linkedTaskIds.FirstOrDefault(id => tasks.Any(task => task.SessionId == id));

            return new RemoteAgentChatListResult(true, "Recent tasks in the linked Codex project.", tasks,
                session.AppId, project, preferred, SupportsRecordingHandoff: true);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteAgentChatListResult(false, DescribeError(error), [], session.AppId,
                SupportsRecordingHandoff: true);
        }
    }

    private static bool BelongsToProject(JsonElement thread, RemoteAgentProject project)
    {
        var assignedProject = ReadString(thread, "projectId");

        if (assignedProject is not null)
        {
            return assignedProject == project.Id;
        }

        return project.Roots.Contains(ReadString(thread, "cwd"), StringComparer.Ordinal);
    }

    public async Task<RemoteAgentTaskListResult> ListAgentTasksAsync(
        RemoteAgentTaskListRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await RefreshDeliveryStatusesAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        var recorded = runtime.Sessions.GetSummaries()
            .Where(session => string.IsNullOrWhiteSpace(request.SessionId)
                || string.Equals(session.SessionId, request.SessionId.Trim(), StringComparison.Ordinal))
            .SelectMany(static session => session.AgentTaskLinks)
            .OrderByDescending(static link => link.UpdatedAtUtc)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .Select(static link => new RemoteAgentTaskLink(
                link.LinkId, link.WorkContextId, link.SessionId, link.AnnotationBatchId,
                link.AnnotationIds, link.FrameIds, link.Source, link.Provider, link.ProviderTaskId,
                link.ProviderTurnId, link.ProviderTaskTitle, link.WorkingDirectory, link.SubmittedPrompt,
                link.Status, link.StatusMessage, link.CreatedAtUtc, link.UpdatedAtUtc))
            .ToArray();
        var tasks = store.ReadReceipts()
            .Select(static receipt => receipt.Link)
            .Where(link => request.SessionId is null || link.SessionId == request.SessionId)
            .Concat(recorded)
            .DistinctBy(static link => link.LinkId)
            .OrderByDescending(static link => link.UpdatedAtUtc)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToArray();

        return new RemoteAgentTaskListResult(true, "Linked Ansight task history loaded.", tasks);
    }

    private static RemoteAgentChatSession ParseThread(JsonElement thread)
    {
        var name = ReadString(thread, "name");
        var preview = ReadString(thread, "preview") ?? "Codex task";
        var title = string.IsNullOrWhiteSpace(name) ? preview.Split('\n')[0] : name;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Review app annotations";
        }

        var hasRecency = thread.TryGetProperty("recencyAt", out var recency)
            && recency.ValueKind == JsonValueKind.Number
            && recency.TryGetInt64(out _);
        var updated = hasRecency ? recency.GetInt64() : thread.GetProperty("updatedAt").GetInt64();
        var branch = thread.TryGetProperty("gitInfo", out var git) ? ReadString(git, "branch") : null;

        return new RemoteAgentChatSession("codex", thread.GetProperty("id").GetString()!,
            title.Length > 160 ? title[..160] : title, ReadString(thread, "cwd"),
            DateTimeOffset.FromUnixTimeSeconds(updated), ReadString(thread.GetProperty("status"), "type") ?? "idle",
            Branch: branch);
    }

    private static string? ReadString(JsonElement value, string property)
    {
        var hasString = value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.String;

        return hasString ? value.GetProperty(property).GetString() : null;
    }

    private static string DescribeError(Exception error)
        => error is OperationCanceledException
            ? "Codex did not respond in time. Retry when the connection is available."
            : error.Message;
}
