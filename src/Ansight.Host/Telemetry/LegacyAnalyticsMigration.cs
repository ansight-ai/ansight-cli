using System.Text.Json;
using Ansight.Analytics;

namespace Ansight.Host.Telemetry;

internal sealed class LegacyAnalyticsMigration
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AnalyticsSettingsStore canonicalSettings;
    private readonly string legacyAnalyticsDirectory;

    public LegacyAnalyticsMigration(string dataDirectory)
    {
        canonicalSettings = new AnalyticsSettingsStore(dataDirectory);
        legacyAnalyticsDirectory = Path.Combine(dataDirectory, "data", "analytics");
    }

    public void Migrate()
    {
        try
        {
            if (!Directory.Exists(legacyAnalyticsDirectory)
                || PathsEqual(legacyAnalyticsDirectory, canonicalSettings.AnalyticsDirectoryPath))
            {
                return;
            }

            var canonicalIdentity = AnalyticsIdentity.LoadOrCreate(canonicalSettings.AnalyticsDirectoryPath);
            var legacyIdentity = TryReadIdentity();
            var canonicalOutbox = new EventOutbox(canonicalSettings.AnalyticsDirectoryPath);
            MigrateOutbox(canonicalOutbox, canonicalIdentity, legacyIdentity);
            if (canonicalSettings.IsDetailedTrackingActive)
            {
                MigrateUsage(canonicalOutbox, canonicalIdentity);
            }
            else
            {
                RemoveLegacyUsage();
            }
            RemoveLegacyDirectoryWhenDrained();
        }
        catch
        {
            // A failed migration leaves the legacy files intact for a later host run.
        }
    }

    private void MigrateOutbox(EventOutbox canonicalOutbox, string canonicalIdentity, string? legacyIdentity)
    {
        var legacyOutbox = new EventOutbox(legacyAnalyticsDirectory);
        foreach (var item in legacyOutbox.Load(250))
        {
            if (item.Envelope.Level == EventLevel.Detailed && !canonicalSettings.IsDetailedTrackingActive)
            {
                legacyOutbox.Remove(item.Path);
                continue;
            }

            var migrated = RewriteEnvelope(item.Envelope, canonicalIdentity, legacyIdentity);
            if (!canonicalOutbox.TryStore(migrated))
            {
                continue;
            }

            if (AcquisitionAnalytics.IsOnceEvent(migrated.EventName))
            {
                File.WriteAllText(
                    Path.Combine(canonicalSettings.AnalyticsDirectoryPath, migrated.InsertId + ".milestone"),
                    "1");
            }

            legacyOutbox.Remove(item.Path);
        }
    }

    private void MigrateUsage(EventOutbox canonicalOutbox, string canonicalIdentity)
    {
        var usageDirectory = Path.Combine(legacyAnalyticsDirectory, "usage");
        if (!Directory.Exists(usageDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(usageDirectory, "*.json").OrderBy(static path => path))
        {
            try
            {
                if (new FileInfo(path).Length > 1024 * 1024)
                {
                    continue;
                }

                var state = JsonSerializer.Deserialize<ProductUsage.State>(File.ReadAllText(path), jsonOptions);
                var envelope = state is null
                    ? null
                    : ProductUsage.CreateSnapshotEnvelope(state, canonicalIdentity, File.GetLastWriteTimeUtc(path));
                if (envelope is not null && canonicalOutbox.TryStore(envelope))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Leave unreadable state for a later run or manual inspection.
            }
        }
    }

    private static EventEnvelope RewriteEnvelope(
        EventEnvelope envelope,
        string canonicalIdentity,
        string? legacyIdentity)
    {
        var properties = new Dictionary<string, object?>(envelope.Properties, StringComparer.Ordinal);
        if (properties.TryGetValue("installation_id", out var installationId)
            && string.Equals(ReadString(installationId), legacyIdentity, StringComparison.Ordinal))
        {
            properties["installation_id"] = canonicalIdentity;
        }
        if (properties.TryGetValue("$anon_distinct_id", out var anonymousId)
            && string.Equals(ReadString(anonymousId), legacyIdentity, StringComparison.Ordinal))
        {
            properties["$anon_distinct_id"] = canonicalIdentity;
        }

        var distinctId = string.Equals(envelope.DistinctId, legacyIdentity, StringComparison.Ordinal)
            ? canonicalIdentity
            : envelope.DistinctId;
        var insertId = AcquisitionAnalytics.IsOnceEvent(envelope.EventName)
            ? AcquisitionAnalytics.CreateOnceInsertId(canonicalIdentity, envelope.EventName)
            : TryCreateUsageInsertId(envelope.EventName, properties, canonicalIdentity)
              ?? envelope.InsertId;
        return envelope with
        {
            InsertId = insertId,
            DistinctId = distinctId,
            Properties = properties
        };
    }

    private void RemoveLegacyUsage()
    {
        var usageDirectory = Path.Combine(legacyAnalyticsDirectory, "usage");
        if (Directory.Exists(usageDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(usageDirectory, "*.json"))
            {
                File.Delete(path);
            }
        }
        RemoveLegacyDirectoryWhenDrained();
    }

    private string? TryReadIdentity()
    {
        var path = Path.Combine(legacyAnalyticsDirectory, "posthog.distinct-id");
        if (!File.Exists(path))
        {
            return null;
        }
        var value = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void RemoveLegacyDirectoryWhenDrained()
    {
        var outboxDirectory = Path.Combine(legacyAnalyticsDirectory, "posthog-outbox");
        var usageDirectory = Path.Combine(legacyAnalyticsDirectory, "usage");
        var hasOutboxEvents = Directory.Exists(outboxDirectory)
                              && Directory.EnumerateFiles(outboxDirectory, "*.json").Any();
        var hasUsageState = Directory.Exists(usageDirectory)
                            && Directory.EnumerateFiles(usageDirectory, "*.json").Any();
        if (!hasOutboxEvents && !hasUsageState)
        {
            Directory.Delete(legacyAnalyticsDirectory, recursive: true);
        }
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? ReadString(object? value)
        => value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => value?.ToString()
        };

    private static string? TryCreateUsageInsertId(
        string eventName,
        IReadOnlyDictionary<string, object?> properties,
        string canonicalIdentity)
    {
        if (eventName != "product_usage_snapshot"
            || !properties.TryGetValue("usage_day", out var dayValue)
            || !properties.TryGetValue("is_ci", out var isCiValue)
            || !properties.TryGetValue("counter_epoch", out var epochValue)
            || !properties.TryGetValue("revision", out var revisionValue))
        {
            return null;
        }

        var day = ReadString(dayValue);
        var epoch = ReadString(epochValue);
        if (string.IsNullOrWhiteSpace(day)
            || string.IsNullOrWhiteSpace(epoch)
            || !TryReadBoolean(isCiValue, out var isCi)
            || !TryReadInt64(revisionValue, out var revision))
        {
            return null;
        }

        return ProductUsage.CreateSnapshotInsertId(canonicalIdentity, day, isCi, epoch, revision);
    }

    private static bool TryReadBoolean(object? value, out bool result)
    {
        if (value is bool boolean)
        {
            result = boolean;
            return true;
        }
        if (value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } element)
        {
            result = element.GetBoolean();
            return true;
        }
        return bool.TryParse(ReadString(value), out result);
    }

    private static bool TryReadInt64(object? value, out long result)
    {
        if (value is long integer)
        {
            result = integer;
            return true;
        }
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element
            && element.TryGetInt64(out result))
        {
            return true;
        }
        return long.TryParse(ReadString(value), out result);
    }
}
