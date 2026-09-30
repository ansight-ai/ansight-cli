namespace Ansight.Host.Explorer;

internal interface IExplorerExtension
{
    object Invoke(object server, string operation, object?[] arguments);
}

internal sealed partial class ExplorerServer
{
private Task<bool> TryHandleAccountsGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (!(route.StartsWith("api/account", StringComparison.Ordinal) || route == "api/access")) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleAccountsGetAsync", [route, request, response, isHead, cancellationToken]);
    }

private Task<bool> TryHandleAccountsPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (!(route.StartsWith("api/account", StringComparison.Ordinal) || route == "api/access")) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleAccountsPostAsync", [route, request, response, cancellationToken]);
    }

private Task<bool> TryHandleCloudGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (!route.StartsWith("api/cloud/", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleCloudGetAsync", [route, request, response, isHead, cancellationToken]);
    }

private Task<SessionUrlResult> ResolveCloudAnalysisSessionAsync(string localSessionId, CancellationToken cancellationToken)
    {

        return (Task<SessionUrlResult>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "ResolveCloudAnalysisSessionAsync", [localSessionId, cancellationToken]);
    }

private Task WriteCloudAnalysisStateAsync(HttpListenerResponse response, string localSessionId, bool isHead, CancellationToken cancellationToken)
    {

        return (Task)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "WriteCloudAnalysisStateAsync", [response, localSessionId, isHead, cancellationToken]);
    }

private Task<bool> TryHandleCloudDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (!route.StartsWith("api/cloud/", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleCloudDynamicPostAsync", [route, request, response, cancellationToken, segments]);
    }

private Task<bool> TryHandleCompanionDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (!route.StartsWith("api/companion", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleCompanionDynamicPostAsync", [route, request, response, cancellationToken, segments]);
    }

private Task<bool> TryHandleCompanionGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (!route.StartsWith("api/companion", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleCompanionGetAsync", [route, request, response, isHead, cancellationToken]);
    }

private Task<bool> TryHandleCompanionPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (!route.StartsWith("api/companion", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, "TryHandleCompanionPostAsync", [route, request, response, cancellationToken]);
    }
    private Task<bool> TryHandleRunnerGetAsync(string route, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (route != "api/runner" || !isExplorer) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, nameof(TryHandleRunnerGetAsync), [route, response, isHead, cancellationToken]);
    }
    private Task<bool> TryHandleRunnerPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (!isExplorer || !route.StartsWith("api/runner/", StringComparison.Ordinal)) return Task.FromResult(false);
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, nameof(TryHandleRunnerPostAsync), [route, request, response, cancellationToken]);
    }
    private Task<bool> TryHandleCloudAnalysisPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        return (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, nameof(TryHandleCloudAnalysisPostAsync), [route, request, response, cancellationToken, segments]);
    }
    private Task<string?> UpdateHostedSettingsAsync(CoreSettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        return (Task<string?>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, nameof(UpdateHostedSettingsAsync), [request, cancellationToken]);
    }
    private Task<bool> TryHandleSessionSharePostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
        => (Task<bool>)runtime.Extensions.GetService<IExplorerExtension>().Invoke(this, nameof(TryHandleSessionSharePostAsync), [route, request, response, cancellationToken, segments]);
}
