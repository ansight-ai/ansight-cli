using Ansight.MacSimulatorHid;

namespace Ansight.Host.UiAutomation;

public interface IStylusInputDriver
{
    Task<UiInputResult> StylusAsync(UiStylusRequest request, CancellationToken cancellationToken = default);
}
