namespace Ansight.Host.Models.Session;

public sealed class SessionTouchSampleDetails
{
    public string? Tool { get; init; }
    public string? SampleKind { get; init; }
    public string? InputSource { get; init; }
    public double? Force { get; init; }
    public double? MaximumPossibleForce { get; init; }
    public double? AltitudeRadians { get; init; }
    public double? AzimuthRadians { get; init; }
    public double? RollRadians { get; init; }
    public long? EstimatedProperties { get; init; }
    public long? EstimatedPropertiesExpectingUpdates { get; init; }
    public long? EstimationUpdateIndex { get; init; }
    public double? Pressure { get; init; }
    public double? TiltRadians { get; init; }
    public double? OrientationRadians { get; init; }
    public double? Distance { get; init; }
    public int? ButtonState { get; init; }
    public double? TouchMajor { get; init; }
    public double? TouchMinor { get; init; }
    public double? ToolMajor { get; init; }
    public double? ToolMinor { get; init; }
}
