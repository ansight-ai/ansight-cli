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

internal sealed partial class ExplorerServer : IAsyncDisposable
{
    private async Task<bool> TryHandleAppsGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/apps" when isExplorer:
                await WriteJsonAsync(response, runtime.Apps.List(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/app-watches" when isExplorer:
                await WriteJsonAsync(response, new { hostRunning = runtime.AppWatches.IsRunning, watches = runtime.AppWatches.List() }, HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/apps/workspace":
                {
                    var appId = request.QueryString["appId"]?.Trim();
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        await WriteJsonAsync(response, new OperationResult(false, "App ID is required."), HttpStatusCode.BadRequest, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    var app = runtime.Apps.Get(appId);
                    if (app is null || string.IsNullOrWhiteSpace(app.CodebasePath))
                    {
                        await WriteJsonAsync(response, new OperationResult(false, $"App '{appId}' does not have a linked workspace."), HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    var workspaceTests = runtime.WorkspaceTests.List(app.CodebasePath, cancellationToken);
                    var tasks = runtime.InspectRepositoryTasks(app.AppId, app.CodebasePath);
                    var automations = runtime.RepositoryAutomations.Inspect(app.AppId, app.CodebasePath);
                    var trendsCatalog = runtime.Trends.List(app.CodebasePath, cancellationToken);
                    var trends = trendsCatalog.Definitions.Where(check => string.Equals(check.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)).ToArray();
                    await WriteJsonAsync(response, new RepositoryWorkspaceCatalog(app.CodebasePath, app.AppId, workspaceTests.Tests.Where(test => string.Equals(test.AppId, app.AppId, StringComparison.OrdinalIgnoreCase)).ToArray(), tasks.Tasks, automations.Triggers, trends, DiscoverWorkspaceSanitizers(app.CodebasePath), workspaceTests.Warnings.Concat(tasks.Warnings).Concat(automations.Warnings).Concat(trendsCatalog.Warnings).Distinct(StringComparer.Ordinal).ToArray()), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/enrollment/invites" when isExplorer:
                await WriteJsonAsync(response, runtime.Pairing.List(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleAppsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (isExplorer && route.StartsWith("api/app-watches/", StringComparison.Ordinal))
            return await TryHandleAppWatchPostAsync(route, request, response, cancellationToken).ConfigureAwait(false);
        switch (route)
        {
            case "api/enrollment/invites" when isExplorer:
                {
                    var body = await ReadJsonAsync<SessionExplorerEnrollmentInviteRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = IssueEnrollmentInvite(body);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.Created : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private SessionExplorerEnrollmentInviteResult IssueEnrollmentInvite(SessionExplorerEnrollmentInviteRequest request)
    {
        var issue = runtime.Pairing.Issue(request.AppId, request.AppName, request.Duration);
        if (!issue.IsSuccess || issue.Invite is null)
        {
            return new SessionExplorerEnrollmentInviteResult(false, issue.Message, issue.Invite, issue.Duration, null, [], null);
        }

        var code = runtime.Pairing.CreateCode(issue.Invite.InviteId, request.HostAddress);
        if (!code.IsSuccess || string.IsNullOrWhiteSpace(code.PairingCode))
        {
            runtime.Pairing.Revoke(issue.Invite.InviteId);
            return new SessionExplorerEnrollmentInviteResult(false, $"{code.Message} The unusable enrollment invite was revoked.", issue.Invite, issue.Duration, null, code.HostAddresses, null);
        }

        try
        {
            var qrImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(PairingQrPngWriter.EncodePng(code.PairingCode));
            return new SessionExplorerEnrollmentInviteResult(true, issue.Message, issue.Invite, issue.Duration, code.PairingCode, code.HostAddresses, qrImageDataUrl);
        }
        catch (InvalidOperationException exception)
        {
            runtime.Pairing.Revoke(issue.Invite.InviteId);
            return new SessionExplorerEnrollmentInviteResult(false, $"Could not create the enrollment QR: {exception.Message} The unusable enrollment invite was revoked.", issue.Invite, issue.Duration, null, code.HostAddresses, null);
        }
    }

    private async Task WriteAppIconAsync(HttpListenerResponse response, string appId, bool isHead, CancellationToken cancellationToken)
    {
        var app = runtime.Apps.Get(appId);
        if (app is null)
        {
            await WriteTextAsync(response, "App not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        var iconPath = !string.IsNullOrWhiteSpace(app.IconImagePath) && File.Exists(app.IconImagePath) ? app.IconImagePath : null;
        if (iconPath is null)
        {
            foreach (var session in runtime.Sessions.GetSummaries().Where(session => string.Equals(session.AppId, app.AppId, StringComparison.Ordinal)).OrderByDescending(static session => session.LastUpdatedUtc))
            {
                iconPath = ResolveSessionIconPath(session);
                if (iconPath is null && session.AppIcon is not null && runtime.SessionCache.ExpandSessionCache(session.SessionId))
                {
                    iconPath = ResolveSessionIconPath(session);
                }

                if (iconPath is not null)
                {
                    break;
                }
            }
        }

        if (iconPath is null)
        {
            await WriteTextAsync(response, "App icon not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteImageFileAsync(response, iconPath, isHead, cancellationToken).ConfigureAwait(false);
    }
}
