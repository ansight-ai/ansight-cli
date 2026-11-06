using Ansight.MacSimulatorHid;

namespace Ansight.Host.UiAutomation;

public sealed record TimedStylusSample(int OffsetMilliseconds, MacSimulatorStylusSample Sample);
