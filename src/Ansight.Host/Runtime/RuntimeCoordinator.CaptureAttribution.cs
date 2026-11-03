namespace Ansight.Host.Runtime;

public sealed partial class RuntimeCoordinator
{
    private void PrepareCaptureAttribution()
    {
        try
        {
            // Loading an opted-in account starts the optional cloud module's membership refresh.
            // Local capture itself never waits for a network request or requires the module.
            if (Extensions.IsAvailable())
            {
                _ = Account.GetStatus();
            }
        }
        catch
        {
            // Missing/expired optional accounts leave company attribution unassigned.
        }
    }
}
