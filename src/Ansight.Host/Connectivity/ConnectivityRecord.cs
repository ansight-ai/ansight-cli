using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Connectivity;

/// <summary>A self-contained observation of the device's default network path.</summary>
public sealed record ConnectivityRecord
{
    public const string SchemaName = "ansight.connectivity.v1";
    public string Schema { get; init; } = SchemaName;
    public required string Id { get; init; }
    public required string ObservationId { get; init; }
    public long Sequence { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public DateTimeOffset? OriginalCapturedAtUtc { get; init; }
    public double ElapsedMilliseconds { get; init; }

    /// <summary>snapshot, change, quality, or gap.</summary>
    public required string Kind { get; init; }
    public required string Source { get; init; }
    public required ConnectivitySnapshot Snapshot { get; init; }
    public ConnectivitySnapshot? PreviousSnapshot { get; init; }
    public ConnectivityQualitySample? Quality { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> SupportedMeasurements { get; init; } = [];
}
