using System.Text.Json.Nodes;

namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceTestingSource
{
    bool SupportsActivity => false;

    Task<RemoteDeviceTestingResult> ExecuteAsync(
        RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default);
}
