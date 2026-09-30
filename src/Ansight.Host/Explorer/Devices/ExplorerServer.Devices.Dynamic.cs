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
    private async Task<bool> TryHandleDevicesDynamicGetAsync(string route, HttpListenerRequest request, HttpListenerResponse response, bool isHead, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 5 && segments[0] == "api" && segments[1] == "devices" && segments[4] == "apps")
        {
            await WriteJsonAsync(response, await runtime.Devices.ListApplicationsAsync(Uri.UnescapeDataString(segments[2]), Uri.UnescapeDataString(segments[3]), cancellationToken).ConfigureAwait(false), HttpStatusCode.OK, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleDevicesDynamicPostAsync(string route, HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken, string[] segments)
    {
        if (isExplorer && segments.Length == 3 && segments[0] == "api" && segments[1] == "devices" && segments[2] is "start" or "shutdown" or "install" or "launch" or "terminate")
        {
            var body = await ReadJsonAsync<LocalDeviceOperationRequest>(request, cancellationToken).ConfigureAwait(false);
            var result = segments[2] switch
            {
                "start" => await runtime.Devices.StartAsync(body.Platform, body.DeviceIdentifier, cancellationToken).ConfigureAwait(false),
                "shutdown" => await runtime.Devices.ShutdownAsync(body.Platform, body.DeviceIdentifier, cancellationToken).ConfigureAwait(false),
                "install" => await runtime.Devices.InstallApplicationAsync(body.Platform, body.DeviceIdentifier, RequireValue(body.ApplicationPath, "An application path is required."), cancellationToken).ConfigureAwait(false),
                "launch" => await runtime.Devices.LaunchApplicationAsync(body.Platform, body.DeviceIdentifier, RequireValue(body.ApplicationIdentifier, "An application identifier is required."), cancellationToken).ConfigureAwait(false),
                "terminate" => await runtime.Devices.TerminateApplicationAsync(body.Platform, body.DeviceIdentifier, RequireValue(body.ApplicationIdentifier, "An application identifier is required."), cancellationToken).ConfigureAwait(false),
                _ => throw new UnreachableException()
            };
            await WriteJsonAsync(response, result, result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict, false, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
