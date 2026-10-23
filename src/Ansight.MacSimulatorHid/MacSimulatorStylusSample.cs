namespace Ansight.MacSimulatorHid;

public enum MacSimulatorStylusPhase
{
    Down, Move, Up, Cancel, HoverEnter, HoverMove, HoverExit
}

/// <summary>UIKit angles in radians; position, pressure and hover distance in [0,1].</summary>
public sealed record MacSimulatorStylusSample(
    MacSimulatorStylusPhase Phase,
    double NormalizedX,
    double NormalizedY,
    double Pressure = 0,
    double AltitudeRadians = Math.PI / 2,
    double AzimuthRadians = 0,
    double RollRadians = 0,
    double Distance = 0)
{
    // Assigned by the transport, never accepted from a pen fixture.
    internal ulong EventTimestampTicks { get; init; }
    public double? EventTimestampSeconds { get; internal init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Phase)
            || !Unit(NormalizedX) || !Unit(NormalizedY) || !Unit(Pressure) || !Unit(Distance)
            || !double.IsFinite(AltitudeRadians) || AltitudeRadians < 0 || AltitudeRadians > Math.PI / 2
            || !double.IsFinite(AzimuthRadians) || !double.IsFinite(RollRadians))
            throw new ArgumentException("Stylus samples require finite normalized coordinates, pressure, distance and UIKit angles.");
    }

    private static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
}
