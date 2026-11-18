using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.LocalSimulator;

internal sealed partial class CliRemoteAgentChatSource
{
    public async Task<RemoteAgentProjectCatalog> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var apps = runtime.Apps.List()
            .Select(app => new RemoteAgentApp(app.AppId, app.Name, app.CodebasePath, store.ReadProject(app.AppId)))
            .ToArray();

        try
        {
            await using var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var projects = new List<RemoteAgentProject>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;

            do
            {
                var result = await connection.CallAsync("project/list", new
                {
                    cursor,
                    limit = 100,
                    sortKey = "recencyAt"
                }, cancellationToken).ConfigureAwait(false);
                projects.AddRange(result.GetProperty("data").EnumerateArray().Select(ParseProject));
                cursor = ReadString(result, "nextCursor");

                if (cursor is not null && !cursors.Add(cursor))
                {
                    throw new InvalidDataException("Codex returned an invalid project page. Retry project discovery.");
                }
            }
            while (cursor is not null);

            return new RemoteAgentProjectCatalog(true, "Codex projects loaded.", apps, projects, true);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Retain the saved links even when Codex is offline, including the
            // ability to unlink a deleted or unavailable project from the phone.
            return new RemoteAgentProjectCatalog(true, DescribeError(error), apps, [], false);
        }
    }

    public async Task<RemoteAgentProjectUpdateResult> UpdateProjectAsync(
        RemoteAgentProjectUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var app = runtime.Apps.Get(request.AppId);

            if (app is null)
            {
                return new RemoteAgentProjectUpdateResult(false, "This app is not registered on the host.");
            }

            if (request.ProjectId is null)
            {
                store.SaveProject(app.AppId, null);

                return new RemoteAgentProjectUpdateResult(true, "Codex project unlinked.");
            }

            await using var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var project = await ReadProjectAsync(connection, request.ProjectId, cancellationToken).ConfigureAwait(false);
            var workingDirectory = request.WorkingDirectory;

            if (workingDirectory is not null && !project.Roots.Contains(workingDirectory, StringComparer.Ordinal))
            {
                return new RemoteAgentProjectUpdateResult(false, "Choose a checkout from this Codex project.");
            }

            workingDirectory ??= project.Roots.FirstOrDefault(root => root == app.CodebasePath)
                ?? project.Roots.FirstOrDefault();
            var link = new RemoteAgentProjectLink(project.Id, project.Name, workingDirectory, DateTimeOffset.UtcNow);
            store.SaveProject(app.AppId, link);

            return new RemoteAgentProjectUpdateResult(true, "Codex project linked to this app on the host.", link);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemoteAgentProjectUpdateResult(false, DescribeError(error));
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private static async Task<RemoteAgentProject> ReadProjectAsync(
        ICodexRpcConnection connection, string projectId, CancellationToken cancellationToken)
    {
        var result = await connection.CallAsync("project/read", new { projectId }, cancellationToken).ConfigureAwait(false);
        var project = ParseProject(result.GetProperty("project"));

        if (project.Id != projectId)
        {
            throw new InvalidDataException("Codex returned a different project. Refresh the project selection.");
        }

        return project;
    }

    private static RemoteAgentProject ParseProject(System.Text.Json.JsonElement project)
        => new(project.GetProperty("id").GetString()!, project.GetProperty("name").GetString()!,
            project.GetProperty("roots").EnumerateArray()
                .Select(static root => root.GetProperty("path").GetString()!)
                .ToArray());
}
