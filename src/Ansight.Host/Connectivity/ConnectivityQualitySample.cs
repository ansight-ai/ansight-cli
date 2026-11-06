using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Connectivity;

public sealed record ConnectivityQualitySample
{
    public DateTimeOffset? OriginalMeasuredAtUtc { get; init; }
    public required DateTimeOffset MeasuredAtUtc { get; init; }
    public required string Source { get; init; }
    public string? LinkQuality { get; init; }
    public double? SignalStrength { get; init; }
    public string? SignalStrengthUnit { get; init; }
    public double? EstimatedDownstreamKbps { get; init; }
    public double? EstimatedUpstreamKbps { get; init; }
}
