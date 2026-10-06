namespace Ansight.RemoteSimulator.Core.Devices;

public interface IRemoteDeviceInventorySource : IRemoteDeviceLifecycleSource
{
    Task<RemoteDeviceInventory> ListInventoryAsync(CancellationToken cancellationToken = default);
}
