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
    private async Task<bool> TryHandleDevicesGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/devices":
                await WriteJsonAsync(response, await runtime.Devices.ListAsync(cancellationToken).ConfigureAwait(false), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
            case "api/location/playback":
                await WriteJsonAsync(response, runtime.DeviceLocationPlayback.GetSnapshot(), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
                return true;
        }

        return false;
    }

    private async Task<bool> TryHandleDevicesPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "api/location/set":
                {
                    var body = await ReadJsonAsync<SessionExplorerSetLocationRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = await runtime.Devices.SetLocationAsync(body.Platform, body.DeviceIdentifier, body.Latitude, body.Longitude, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/location/clear":
                {
                    var body = await ReadJsonAsync<SessionExplorerClearLocationRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = await runtime.Devices.ClearLocationAsync(body.Platform, body.DeviceIdentifier, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/location/route":
                {
                    var body = await ReadJsonAsync<DeviceLocationPlaybackRequest>(request, cancellationToken).ConfigureAwait(false);
                    var result = await runtime.DeviceLocationPlayback.StartAsync(body, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case "api/location/route/stop":
                {
                    var result = await runtime.DeviceLocationPlayback.StopAsync(cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
                    return true;
                }
        }

        return false;
    }

    private async Task ProxySimulatorFrameAsync(HttpListenerResponse response, string sessionId, bool isHead, CancellationToken cancellationToken)
    {
        var companion = runtime.LocalSimulatorControl;
        if (companion is not null)
        {
            await companion.RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        }

        var resolved = TryResolveLiveSimulatorTarget(sessionId, out var target, out var error);
        if (!resolved && companion is not null)
        {
            await companion.RefreshDevicesNowAsync(cancellationToken).ConfigureAwait(false);
            resolved = TryResolveLiveSimulatorTarget(sessionId, out target, out error);
        }
        if (!resolved)
        {
            await WriteTextAsync(response, error, HttpStatusCode.Conflict, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ProxySimulatorRequestAsync(response, target.RemoteControlBaseUrl, "/api/frame", HttpMethod.Get, null, $"udid={Uri.EscapeDataString(target.DeviceIdentifier)}", isHead, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProxySimulatorCommandAsync(HttpListenerResponse response, string sessionId, string operation, JsonObject body, CancellationToken cancellationToken)
    {
        var companion = runtime.LocalSimulatorControl;
        if (companion is not null)
        {
            await companion.RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        }

        var resolved = TryResolveLiveSimulatorTarget(sessionId, out var target, out var error);
        if (!resolved && companion is not null)
        {
            await companion.RefreshDevicesNowAsync(cancellationToken).ConfigureAwait(false);
            resolved = TryResolveLiveSimulatorTarget(sessionId, out target, out error);
        }
        if (!resolved)
        {
            await WriteJsonAsync(response, OperationResult.Failure(error), HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        string endpoint;
        string? query = null;
        switch (operation)
        {
            case "offer":
                endpoint = "/api/webrtc/offer";
                body["deviceUdid"] = target.DeviceIdentifier;
                body["grantedScopes"] = new JsonArray();
                break;
            case "close":
                endpoint = "/api/webrtc/close";
                var remoteSessionId = ReadOptionalString(body, "sessionId");
                if (string.IsNullOrWhiteSpace(remoteSessionId))
                {
                    throw new InvalidDataException("A simulator control session ID is required.");
                }

                query = $"session={Uri.EscapeDataString(remoteSessionId.Trim())}";
                break;
            case "input":
                endpoint = "/api/input";
                body["udid"] = target.DeviceIdentifier;
                break;
            case "button":
                endpoint = "/api/button";
                body["udid"] = target.DeviceIdentifier;
                break;
            case "key":
                endpoint = "/api/key";
                body["udid"] = target.DeviceIdentifier;
                break;
            case "text":
                endpoint = "/api/text";
                body["udid"] = target.DeviceIdentifier;
                break;
            default:
                await WriteTextAsync(response, "Simulator command not found.", HttpStatusCode.NotFound, false, cancellationToken).ConfigureAwait(false);
                return;
        }

        await ProxySimulatorRequestAsync(response, target.RemoteControlBaseUrl, endpoint, HttpMethod.Post, body, query, false, cancellationToken).ConfigureAwait(false);
    }

    private bool TryResolveLiveSimulatorTarget(string sessionId, out LiveSimulatorTarget target, out string error)
    {
        var session = runtime.Sessions.GetSummaries().FirstOrDefault(candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal));
        if (session is null || !runtime.IsSessionLive(sessionId))
        {
            target = LiveSimulatorTarget.Empty;
            error = "The selected session is not live.";
            return false;
        }

        var deviceIdentifier = ResolveRuntimeDeviceIdentifier(session);
        var isVirtual = session.DeviceProfile?.Device?.IsVirtual == true || session.DeviceProfile?.Device?.IsEmulator == true || deviceIdentifier?.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrWhiteSpace(deviceIdentifier) && runtime.Devices.TryGetKnownDevice(deviceIdentifier, out _, out var kind))
        {
            isVirtual |= DeviceKinds.IsVirtual(kind);
        }

        if (string.IsNullOrWhiteSpace(deviceIdentifier) || !isVirtual)
        {
            target = LiveSimulatorTarget.Empty;
            error = "The selected live session is not running in a simulator or emulator.";
            return false;
        }

        var companion = runtime.LocalSimulatorControl;
        var remoteControlBaseUrl = companion?.LoopbackBaseUrl;
        if (companion is null || !companion.IsRunning || string.IsNullOrWhiteSpace(remoteControlBaseUrl))
        {
            target = LiveSimulatorTarget.Empty;
            error = "Simulator control is not currently available from the local host.";
            return false;
        }

        if (!companion.TryResolveBootedDeviceIdentifier(
                deviceIdentifier, out var transportDeviceIdentifier, out error))
        {
            target = LiveSimulatorTarget.Empty;
            return false;
        }

        target = new LiveSimulatorTarget(transportDeviceIdentifier, remoteControlBaseUrl);
        error = string.Empty;
        return true;
    }

    private string? ResolveRuntimeDeviceIdentifier(AppSessionSnapshot session)
    {
        var reportedIdentifier = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(session);
        var companion = runtime.LocalSimulatorControl;
        return reportedIdentifier is not null
               && runtime.IsSessionLive(session.SessionId)
               && companion is not null
               && companion.TryResolveBootedDeviceIdentifier(
                   reportedIdentifier, out var transportIdentifier, out _)
            ? transportIdentifier
            : reportedIdentifier;
    }

    private static async Task ProxySimulatorRequestAsync(HttpListenerResponse response, string remoteControlBaseUrl, string endpoint, HttpMethod method, JsonObject? body, string? additionalQuery, bool isHead, CancellationToken cancellationToken)
    {
        var baseUri = new Uri(remoteControlBaseUrl);
        var authenticationQuery = baseUri.Query.TrimStart('?');
        var combinedQuery = string.IsNullOrWhiteSpace(additionalQuery) ? authenticationQuery : $"{authenticationQuery}&{additionalQuery}";
        var targetUri = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, endpoint)
        {
            Query = combinedQuery
        }.Uri;
        using var proxyRequest = new HttpRequestMessage(method, targetUri);
        if (body is not null)
        {
            proxyRequest.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, jsonOptions));
            proxyRequest.Content.Headers.ContentType = new("application/json");
        }

        using var proxyResponse = await simulatorHttpClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var bytes = await proxyResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        await WriteBytesAsync(response, bytes, proxyResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", proxyResponse.StatusCode, isHead, cancellationToken).ConfigureAwait(false);
    }
}
