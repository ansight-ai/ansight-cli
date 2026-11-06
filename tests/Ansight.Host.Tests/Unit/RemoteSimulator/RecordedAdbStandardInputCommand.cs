using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Simulator.Android;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

internal sealed record RecordedAdbStandardInputCommand(
    IReadOnlyList<string> Arguments,
    string StandardInput);
