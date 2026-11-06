using System.Text.Json.Nodes;

namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceTestingSource
{
    Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteDeviceTestingRequest(
    string DeviceUdid,
    string Action,
    string? AppId = null,
    string? Preset = null,
    string? LeaseId = null,
    bool? Online = null,
    long? Sequence = null,
    string? RouteContent = null,
    double SpeedKph = 30,
    string? RunId = null,
    bool RecordedTiming = false,
    string? Platform = null,
    string? Permission = null,
    string? Value = null,
    string? Payload = null,
    string? Prompt = null,
    string? ExecutionType = null,
    string? RequestId = null);

public sealed record RemoteDeviceTestingResult(
    bool IsSuccess,
    string Message,
    JsonObject? Details = null);
