using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ansight.Host.AppGraphs;
using Ansight.Host.Files;
using Ansight.Host.Trends;
using Ansight.Host.Runtime.BinaryTransfers;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleAppsDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "apps" && segments[3] == "automations")
        {
            var appId = Uri.UnescapeDataString(segments[2]);
            var app = runtime.Apps.Get(appId);
            if (app is null || string.IsNullOrWhiteSpace(app.CodebasePath))
            {
                await WriteJsonAsync(response, OperationResult.Failure($"App '{appId}' does not have a linked workspace."), HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await WriteJsonAsync(response, new { connection = runtime.RepositoryAutomations.Inspect(appId, app.CodebasePath), recentRuns = runtime.RepositoryAutomations.GetRecentRuns(appId) }, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (segments.Length == 4 && segments[0] == "api" && segments[1] == "apps" && segments[3] == "icon" && isExplorer)
        {
            await WriteAppIconAsync(response, Uri.UnescapeDataString(segments[2]), isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleAppsDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 3 && segments[0] == "api" && segments[1] == "apps" && segments[2] == "register")
        {
            var body = await ReadJsonAsync<LocalAppRegistrationRequest>(request, cancellationToken).ConfigureAwait(false);
            var registration = new AppRegistrationRequest(body.AppId, body.Name, body.CodebasePath);
            var result = string.IsNullOrWhiteSpace(body.PreviousAppId) ? runtime.Apps.Register(registration) : runtime.Apps.UpdateRegistration(body.PreviousAppId, registration);
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 4 && segments[0] == "api" && segments[1] == "apps" && segments[3] == "remove")
        {
            var result = runtime.Apps.Remove(Uri.UnescapeDataString(segments[2]));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 5 && segments[0] == "api" && segments[1] == "apps" && segments[3] == "workspace" && segments[4] is "link" or "unlink")
        {
            var appId = Uri.UnescapeDataString(segments[2]);
            var result = segments[4] == "unlink" ? runtime.Apps.ClearRepositoryWorkspace(appId) : runtime.Apps.ConfigureRepositoryWorkspace(appId, (await ReadJsonAsync<LocalAppAutomationRequest>(request, cancellationToken).ConfigureAwait(false)).RepositoryRootPath ?? throw new InvalidDataException("A repository workspace path is required."));
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (isExplorer && segments.Length == 5 && segments[0] == "api" && segments[1] == "apps" && segments[3] == "automations" && segments[4] is "connect" or "disconnect")
        {
            var appId = Uri.UnescapeDataString(segments[2]);
            object result;
            bool isSuccess;
            if (segments[4] == "connect")
            {
                var body = await ReadJsonAsync<LocalAppAutomationRequest>(request, cancellationToken).ConfigureAwait(false);
                var app = runtime.Apps.Get(appId);
                var rootPath = body.RepositoryRootPath ?? app?.CodebasePath;
                if (string.IsNullOrWhiteSpace(rootPath))
                {
                    throw new InvalidDataException("A repository workspace path is required.");
                }

                var appUpdate = runtime.Apps.ConfigureRepositoryWorkspace(appId, rootPath);
                if (!appUpdate.IsSuccess)
                {
                    result = appUpdate;
                    isSuccess = false;
                }
                else
                {
                    var connection = runtime.RepositoryAutomations.Connect(appId, rootPath);
                    runtime.Apps.SetRepositoryAutomationsEnabled(appId, connection.IsConnected);
                    result = connection;
                    isSuccess = connection.IsConnected;
                }
            }
            else
            {
                runtime.RepositoryAutomations.Disconnect(appId);
                var appUpdate = runtime.Apps.SetRepositoryAutomationsEnabled(appId, false);
                result = appUpdate;
                isSuccess = appUpdate.IsSuccess;
            }

            await WriteJsonAsync(response, result, isSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
