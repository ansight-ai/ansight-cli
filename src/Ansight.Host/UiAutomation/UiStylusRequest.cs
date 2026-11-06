using Ansight.MacSimulatorHid;

namespace Ansight.Host.UiAutomation;

public sealed record UiStylusRequest(string SessionId, string DeviceIdentifier, IReadOnlyList<TimedStylusSample> Samples)
{
    internal Func<MacSimulatorStylusSample, CancellationToken, Task>? BeforeSample { get; init; }
}
