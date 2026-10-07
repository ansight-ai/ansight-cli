namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private GettingStartedStore GettingStarted => new(runtime.ApplicationPaths.ApplicationDataPath);

    private async Task<bool> TryHandleGettingStartedGetAsync(
        string route, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (route != "api/getting-started" || !isExplorer) return false;
        await WriteJsonAsync(response, GettingStarted.Read(), HttpStatusCode.OK, isHead, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleGettingStartedPostAsync(
        string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (route != "api/getting-started" || !isExplorer) return false;
        var update = await ReadJsonAsync<GettingStartedUpdate>(request, cancellationToken).ConfigureAwait(false);
        var state = update.Action switch
        {
            "open" => GettingStarted.Update(current => current with { Opened = true }),
            "skip" => GettingStarted.Update(current => current with { Opened = true, Skipped = true }),
            "resume" => GettingStarted.Update(current => current with { Opened = true, Skipped = false }),
            "choose-sdk" => GettingStarted.Update(current => current with { CapturePath = "sdk", Opened = true }),
            "choose-external" => GettingStarted.Update(current => current with { CapturePath = "external", Opened = true }),
            "replay" when !string.IsNullOrWhiteSpace(update.SessionId)
                && runtime.Sessions.GetSummaries().Any(session =>
                    session.SessionId == update.SessionId && session.TotalImageCount > 0
                    && !runtime.IsSessionLive(session.SessionId))
                => GettingStarted.Update(current => current with { ReplayedSessionId = update.SessionId }),
            "automation-saved" when !string.IsNullOrWhiteSpace(update.SessionId)
                && runtime.Sessions.GetSummaries().Any(session => session.SessionId == update.SessionId)
                => GettingStarted.Update(current => current with { AutomationSaved = true }),
            _ => throw new InvalidDataException("Unknown or unavailable getting started action.")
        };
        await WriteJsonAsync(response, state, HttpStatusCode.OK, false, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private sealed record GettingStartedUpdate(string Action, string? SessionId = null);
}
