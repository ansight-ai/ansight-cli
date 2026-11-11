using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ansight.RemoteSimulator.Core.Devices;
using static Ansight.RemoteSimulator.Core.Server.RemoteControlHttpTransport;

namespace Ansight.RemoteSimulator.Core.Server;

public sealed partial class RemoteControlServer
{
    private async Task HandleDeviceTestingAsync(
        NetworkStream stream,
        byte[] body,
        CancellationToken cancellationToken)
    {
        if (deviceTestingSource is null)
        {
            await WriteJsonAsync(stream, HttpStatusCode.ServiceUnavailable,
                new ErrorResponse("Device testing is unavailable on this host."), cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        RemoteDeviceTestingRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<RemoteDeviceTestingRequest>(body, jsonOptions);
        }
        catch (JsonException)
        {
            request = null;
        }

        var validRequest = request is not null
            && !string.IsNullOrWhiteSpace(request.DeviceUdid)
            && request.Action is "shake" or "network-capabilities" or "network-set" or "network-get" or "network-reset"
                or "network-follow" or "network-heartbeat"
                or "route-start" or "route-status" or "route-stop"
                or "app-capabilities" or "permission-grant" or "permission-deny" or "permission-reset"
                or "app-background" or "app-resume" or "app-restart" or "push" or "open-link"
                or "display-rotation" or "display-appearance" or "display-text-size"
                or "task-start" or "task-status" or "task-cancel" or "device-activity";

        if (!validRequest)
        {
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest,
                new ErrorResponse("A device identifier and supported testing action are required."), cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        if (request!.Action != "device-activity")
        {
            await RefreshDevicesAsync(cancellationToken).ConfigureAwait(false);
        }

        // A lease must remain resettable even after its target shuts down.
        var needsBootedDevice = request!.Action is not ("network-get" or "network-reset" or "route-stop" or "route-status"
            or "task-status" or "task-cancel" or "device-activity");

        if (needsBootedDevice && !IsBootedDevice(request.DeviceUdid))
        {
            await WriteJsonAsync(stream, HttpStatusCode.NotFound,
                new ErrorResponse("The selected simulator or emulator is not booted."), cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var device = runtimeSource.Current.Devices.FirstOrDefault(device =>
            string.Equals(device.Identifier, request.DeviceUdid, StringComparison.OrdinalIgnoreCase));
        request = request with
        {
            Platform = device?.Platform
        };

        var result = await deviceTestingSource.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(stream,
            result.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.Conflict,
            result.IsSuccess ? (object)result : new ErrorResponse(result.Message), cancellationToken)
            .ConfigureAwait(false);
    }
}
