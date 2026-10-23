using Ansight.MacSimulatorHid;

namespace Ansight.Host.UiAutomation;

public sealed record TimedStylusSample(int OffsetMilliseconds, MacSimulatorStylusSample Sample);
public sealed record UiStylusRequest(string SessionId, string DeviceIdentifier, IReadOnlyList<TimedStylusSample> Samples)
{
    internal Func<MacSimulatorStylusSample, CancellationToken, Task>? BeforeSample { get; init; }
}
public interface IStylusInputDriver
{
    Task<UiInputResult> StylusAsync(UiStylusRequest request, CancellationToken cancellationToken = default);
}
