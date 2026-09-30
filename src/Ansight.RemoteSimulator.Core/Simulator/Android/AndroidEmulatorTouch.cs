namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed record AndroidEmulatorTouch(
    int X,
    int Y,
    int Identifier,
    int Pressure);
