using System.Text.Json.Nodes;

namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceTestingSource
{
    Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default);
}
