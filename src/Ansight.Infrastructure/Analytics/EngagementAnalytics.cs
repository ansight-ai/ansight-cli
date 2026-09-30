using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Analytics;

/// <summary>Mandatory, coarse local engagement measurement shared by the CLI and player.</summary>
public static class EngagementAnalytics
{
    private const int ActionsRequired = 2;
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static void RecordDailySignal(string dataDirectory, EventEnvelope envelope)
    {
        if (envelope.Level != EventLevel.Daily || envelope.EventName is not
            ("cli_daily_active" or "cli_command_used_daily" or "local_web_daily_active")) return;

        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            var directory = Path.Combine(settings.AnalyticsDirectoryPath, "engagement");
            Directory.CreateDirectory(directory);
            using var guard = new FileStream(Path.Combine(directory, "daily.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(directory, "daily-state.json");
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<DailyState>(File.ReadAllText(path), jsonOptions) ?? new DailyState()
                : new DailyState();
            var now = envelope.TimestampUtc.ToUniversalTime();
            if (state.LastSent.TryGetValue(envelope.EventName, out var previous)
                && (envelope.EventName == "cli_command_used_daily"
                    ? now - previous < TimeSpan.FromHours(24)
                    : now.Date == previous.Date)) return;

            if (!new EventOutbox(settings.AnalyticsDirectoryPath).TryStore(envelope)) return;
            state.LastSent[envelope.EventName] = now;
            File.WriteAllText(path, JsonSerializer.Serialize(state, jsonOptions));
        }
        catch
        {
            // Engagement delivery is best effort and cannot block local work.
        }
    }

    public static void RecordAction(string dataDirectory, DateTimeOffset timestampUtc)
    {
        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            var directory = Path.Combine(settings.AnalyticsDirectoryPath, "engagement");
            Directory.CreateDirectory(directory);
            var hour = timestampUtc.ToUniversalTime().ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, hour + ".json");
            using var guard = new FileStream(Path.Combine(directory, hour + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<HourlyState>(File.ReadAllText(path), jsonOptions) ?? new HourlyState()
                : new HourlyState();
            if (state.Emitted) return;

            state.Actions = Math.Min(ActionsRequired, state.Actions + 1);
            if (state.Actions == ActionsRequired)
            {
                var identity = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
                var insertId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{identity}:local_engaged_hourly:{hour}")))[..32].ToLowerInvariant();
                state.Emitted = new EventOutbox(settings.AnalyticsDirectoryPath).TryStore(new EventEnvelope(
                    insertId, "local_engaged_hourly", identity, EventLevel.Daily,
                    new Dictionary<string, object?>
                    {
                        ["surface"] = "local",
                        ["tracking_level"] = "engagement",
                        ["minimum_actions"] = ActionsRequired
                    }, timestampUtc.ToUniversalTime()));
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, jsonOptions));
        }
        catch
        {
            // Storage contention and telemetry failures must never affect local tools.
        }
    }

    private sealed class HourlyState
    {
        public int Actions { get; set; }
        public bool Emitted { get; set; }
    }

    private sealed class DailyState
    {
        public Dictionary<string, DateTimeOffset> LastSent { get; set; } = new(StringComparer.Ordinal);
    }
}
