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

public sealed record ConnectivitySnapshot
{
    public string Availability { get; init; } = "unknown";
    public IReadOnlyList<string> Transports { get; init; } = [];
    public bool? Vpn { get; init; }
    public bool? InternetValidated { get; init; }
    public bool? CaptivePortal { get; init; }
    public bool? Metered { get; init; }
    public bool? Expensive { get; init; }
    public bool? LowDataMode { get; init; }
}

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

/// <summary>Canonical wire encoding, also carried in connectivity.* application events.</summary>
public static class ConnectivityJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ConnectivityRecord record)
    {
        if (!IsValid(record)) throw new ArgumentException("Invalid connectivity record.", nameof(record));
        return JsonSerializer.Serialize(record, Options);
    }

    public static ConnectivityRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_768) return null;
        try
        {
            var record = JsonSerializer.Deserialize<ConnectivityRecord>(json, Options);
            return record is not null && IsValid(record) ? record : null;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsValid(ConnectivityRecord record)
        => record.Schema == ConnectivityRecord.SchemaName
           && !string.IsNullOrWhiteSpace(record.Id) && record.Id.Length <= 128
           && !string.IsNullOrWhiteSpace(record.ObservationId) && record.ObservationId.Length <= 128
           && record.Sequence >= 0 && double.IsFinite(record.ElapsedMilliseconds) && record.ElapsedMilliseconds >= 0
           && record.CapturedAtUtc != default
           && record.Kind is "snapshot" or "change" or "quality" or "gap"
           && !string.IsNullOrWhiteSpace(record.Source) && record.Source.Length <= 128
           && ValidSnapshot(record.Snapshot) && (record.PreviousSnapshot is null || ValidSnapshot(record.PreviousSnapshot))
           && (record.Kind != "gap" || record.Snapshot.Availability == "unknown")
           && (record.Kind != "change" || record.PreviousSnapshot is not null)
           && (record.Kind != "quality" || record.Quality is not null)
           && record.SupportedMeasurements is { Count: <= 32 }
           && record.SupportedMeasurements.All(value => value is { Length: > 0 and <= 128 })
           && record.Reason is not { Length: > 256 }
           && (record.Quality is null || ValidQuality(record.Quality));

    private static bool ValidSnapshot(ConnectivitySnapshot? snapshot)
        => snapshot is not null && snapshot.Availability is "available" or "unavailable" or "unknown"
           && snapshot.Transports is { Count: <= 16 }
           && snapshot.Transports.All(value => value is "wifi" or "cellular" or "ethernet" or "bluetooth" or "other");

    private static bool ValidQuality(ConnectivityQualitySample quality)
        => quality.MeasuredAtUtc != default && quality.Source is { Length: > 0 and <= 128 }
           && quality.LinkQuality is null or "unknown" or "minimal" or "moderate" or "good"
           && (quality.SignalStrength is null || (double.IsFinite(quality.SignalStrength.Value)
               && quality.SignalStrengthUnit is "dBm" or "level" or "bearer-specific"))
           && NonNegative(quality.EstimatedDownstreamKbps) && NonNegative(quality.EstimatedUpstreamKbps);

    private static bool NonNegative(double? value) => value is null || (double.IsFinite(value.Value) && value.Value >= 0);
}
