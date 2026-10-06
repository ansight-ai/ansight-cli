namespace Ansight.Adb;

/// <summary>Acceleration reported by an Android emulator, in metres per second squared.</summary>
public readonly record struct EmulatorAcceleration(double X, double Y, double Z);
