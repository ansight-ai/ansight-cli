namespace Ansight.Host.Explorer;
internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleLocalGraphsGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch(route)
        {
            case "api/app-graph-runs" when isExplorer:
                await WriteJsonAsync(response, runtime.SimulatorAgent.ListAppGraphLiveRuns(request.QueryString["sessionId"]), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/app-graphs" when isExplorer:
                await WriteJsonAsync(response, runtime.LocalAppGraphs.List(request.QueryString["appId"]), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/app-graph-recordings" when isExplorer:
                await WriteJsonAsync(response, runtime.LocalAppGraphs.ListRecordings(request.QueryString["sessionId"]), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/app-graph-navigation-controllers" when isExplorer:
                await WriteJsonAsync(response, await LocalAppGraphNavigationDiscovery.DiscoverAsync(runtime.AppTools, request.QueryString["sessionId"], cancellationToken).ConfigureAwait(false), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }
        return false;
    }

    private async Task<bool> TryHandleLocalGraphsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/app-graph-recordings/start" when isExplorer:
                {
                    var body = await ReadJsonAsync<LocalAppGraphRecordingStartRequest>(request, cancellationToken).ConfigureAwait(false);
                    if (!runtime.Sessions.TryGetLiveContentSnapshot(body.SessionId, out var snapshot) || snapshot is null)
                    {
                        await WriteJsonAsync(response, LocalAppGraphRecordingOperationResult.Failure("Choose a connected live session."), HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    var result = runtime.LocalAppGraphs.StartRecording(body with { AppId = snapshot.AppId, AppName = snapshot.Name ?? snapshot.ClientName });
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.Created : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

}
