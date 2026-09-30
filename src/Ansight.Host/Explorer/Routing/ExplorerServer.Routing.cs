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
    private async Task<bool> TryHandleGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        if (route.StartsWith("api/extensions/", StringComparison.Ordinal) && runtime.Extensions.ResolveUiAsset(route) is { } assetPath)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = Path.GetExtension(assetPath).ToLowerInvariant() switch
            {
                ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
                ".woff2" => "font/woff2", ".svg" => "image/svg+xml", _ => "application/octet-stream"
            };
            var bytes = await File.ReadAllBytesAsync(assetPath, cancellationToken).ConfigureAwait(false);
            response.ContentLength64 = bytes.Length;
            if (!isHead) await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            response.Close();
            return true;
        }
        if (await TryHandleRunnerGetAsync(route, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleAccountsGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTransportGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleSessionsGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleLocalGraphsGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false)) return true;
        if (await TryHandleCloudGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleAppsGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleConfigurationGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTestsGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleDevicesGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleCompanionGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTaskExtractionGetAsync(route, request, response, isHead, cancellationToken).ConfigureAwait(false))
            return true;
        if (route.StartsWith("session-replay-", StringComparison.Ordinal) && TryResolveReplayAssetContentType(route, out var replayAssetContentType) && TryLoadAsset(route, out var additionalAsset))
        {
            await WriteReplayAssetAsync(request, response, additionalAsset, replayAssetContentType, true, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (route.StartsWith("frames/", StringComparison.Ordinal) && InitialSessionId is not null)
        {
            var frameId = Uri.UnescapeDataString(route["frames/".Length..]);
            await WriteFrameAsync(response, InitialSessionId, frameId, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (await TryHandleTaskExtractionDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleDevicesDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleAppsDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleSessionsDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleTestsDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleTransportDynamicGetAsync(route, request, response, isHead, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandlePostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        if (await TryHandleRunnerPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleSessionsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleAccountsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleCompanionPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTestsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTaskExtractionPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleTransportPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleLocalGraphsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false)) return true;
        if (await TryHandleDevicesPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleAppsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleDiagnosticsPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        if (await TryHandleConfigurationPostAsync(route, request, response, cancellationToken).ConfigureAwait(false))
            return true;
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (await TryHandleSessionsDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleTaskExtractionDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleTransportDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleDevicesDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleAppsDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleCompanionDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleTestsDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        if (await TryHandleCloudDynamicPostAsync(route, request, response, cancellationToken, segments).ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }
}
