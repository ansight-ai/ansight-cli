using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.AppInspection;

public interface IRemoteAppInspectionSource
{
    Task<RemoteAppInspectionResult> InvokeAsync(
        string deviceUdid,
        RemoteAppInspectionRequest request,
        CancellationToken cancellationToken = default);
}
