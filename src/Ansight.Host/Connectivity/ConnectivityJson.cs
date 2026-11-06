using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Connectivity;

/// <summary>Canonical wire encoding, also carried in connectivity.* application events.</summary>
public static class ConnectivityJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ConnectivityRecord record)
    {
        if (!IsValid(record))
        {
            throw new ArgumentException("Invalid connectivity record.", nameof(record));
        }

        return JsonSerializer.Serialize(record, Options);
    }

    public static ConnectivityRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_768)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<ConnectivityRecord>(json, Options);

            return record is not null && IsValid(record) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
            && record.SupportedMeasurements is { Count: <= 32 } && record.SupportedMeasurements.All(value => value is { Length: > 0 and <= 128 })
            && record.Reason is not { Length: > 256 } && (record.Quality is null || ValidQuality(record.Quality));

    private static bool ValidSnapshot(ConnectivitySnapshot? snapshot)
        => snapshot is not null && snapshot.Availability is "available" or "unavailable" or "unknown"
            && snapshot.Transports is { Count: <= 16 } && snapshot.Transports.All(value => value is "wifi" or "cellular" or "ethernet" or "bluetooth" or "other");

    private static bool ValidQuality(ConnectivityQualitySample quality)
        => quality.MeasuredAtUtc != default && quality.Source is { Length: > 0 and <= 128 } && quality.LinkQuality is null or "unknown" or "minimal" or "moderate" or "good"
            && (quality.SignalStrength is null || (double.IsFinite(quality.SignalStrength.Value)
                && quality.SignalStrengthUnit is "dBm" or "level" or "bearer-specific"))
            && NonNegative(quality.EstimatedDownstreamKbps) && NonNegative(quality.EstimatedUpstreamKbps);

    private static bool NonNegative(double? value) => value is null || (double.IsFinite(value.Value) && value.Value >= 0);
}
