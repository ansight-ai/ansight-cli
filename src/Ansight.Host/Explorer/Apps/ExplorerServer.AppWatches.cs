using System.Net;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleAppWatchPostAsync(string route, HttpListenerRequest request,
        HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var segments = route.Split('/');
        string message;
        if (segments.Length == 3 && segments[2] == "add")
        {
            var body = await ReadJsonAsync<LocalAppWatchRequest>(request, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.AppId)) throw new ArgumentException("An app bundle/package identifier is required.");
            if (body.CaptureFiles is null) throw new ArgumentException("Capture files must be a list of sandbox paths.");
            await runtime.AppWatches.AddAsync(body.AppId, body.Platform, body.DeviceId, body.CaptureFiles,
                cancellationToken, body.CaptureInstruments, body.ScreenshotIntervalMilliseconds).ConfigureAwait(false);
            message = body.DeviceId is null
                ? $"Monitoring enabled for {body.AppId.Trim()}. Matching virtual devices are discovered automatically."
                : $"Monitoring enabled for {body.AppId.Trim()} on {body.DeviceId.Trim()}.";
        }
        else if (segments.Length == 4 && segments[3] == "configure")
        {
            var body = await ReadJsonAsync<LocalAppWatchConfigurationRequest>(request, cancellationToken).ConfigureAwait(false);
            if (body.ScreenshotIntervalMilliseconds is not { } interval)
                throw new ArgumentException("A screenshot interval in milliseconds is required.");
            await runtime.AppWatches.SetScreenshotIntervalAsync(Uri.UnescapeDataString(segments[2]),
                interval, cancellationToken).ConfigureAwait(false);
            message = "Screenshot interval saved. Active captures use the new interval immediately.";
        }
        else if (segments.Length == 4 && segments[3] is "enable" or "disable" or "remove")
        {
            var id = Uri.UnescapeDataString(segments[2]);
            if (segments[3] == "remove")
                await runtime.AppWatches.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
            else
                await runtime.AppWatches.SetEnabledAsync(id, segments[3] == "enable", cancellationToken).ConfigureAwait(false);
            message = segments[3] switch
            {
                "enable" => "App monitoring enabled.",
                "disable" => "App monitoring disabled. Captured sessions are retained.",
                _ => "App monitor removed. Captured sessions are retained."
            };
        }
        else return false;

        await WriteJsonAsync(response, new
        {
            isSuccess = true, message, hostRunning = runtime.AppWatches.IsRunning, watches = runtime.AppWatches.List()
        }, HttpStatusCode.OK, false, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

internal sealed record LocalAppWatchRequest(string AppId, string? Platform = null, string? DeviceId = null)
{
    public string[] CaptureFiles { get; init; } = [];
    public bool CaptureInstruments { get; init; }
    public int? ScreenshotIntervalMilliseconds { get; init; }
}

internal sealed record LocalAppWatchConfigurationRequest(int? ScreenshotIntervalMilliseconds);
