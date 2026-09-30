using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Analytics;

/// <summary>Fixed-category numeric usage counters. Accepts only fixed categories and an opaque authenticated account UUID.
/// Snapshots are cumulative per UTC day; reporting must select the latest revision, not sum snapshots.</summary>
public static class ProductUsage
{
    public static readonly IReadOnlySet<string> Features = new HashSet<string>(StringComparer.Ordinal)
    {
        "capture", "logs", "network", "visual_tree", "artifacts", "files", "playback", "annotations",
        "session", "export", "import", "share", "cloud", "device", "ui", "keyboard", "audio", "location",
        "app", "pairing", "companion", "app_graph", "graph_recording", "graph_saved", "graph_explore", "graph_run",
        "task", "task_extract", "task_saved", "test", "test_batch", "batch_test", "automation", "replay_plan", "replay", "profile", "profile_dotnet", "profile_ios", "profile_android", "profile_sample", "trends", "workspace",
        "host", "health", "settings", "account", "about", "test_history", "session_admin", "active_minute", "business_activity"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> metricKeys = new[] { "host", "cli", "local_web", "local_api" }
        .SelectMany(surface => Features.SelectMany(feature => new[] { "count", "observed", "succeeded", "failed", "cancelled", "blocked", "finding", "skipped", "seconds", "max_seconds", "with_evidence" }
            .Select(suffix => $"{surface}_{feature}_{suffix}"))).ToFrozenSet(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> lastFlush = new();

    public static void Record(string dataDirectory, string feature, string surface = "host", string outcome = "observed",
        double durationSeconds = 0, bool hasEvidence = false, int count = 1)
    {
        // Validate before touching disk. Strings here are classifications, never caller-provided labels.
        if (count <= 0 || count > 100000 || !Features.Contains(feature) || surface is not ("cli" or "local_web" or "host" or "local_api")
            || outcome is not ("observed" or "succeeded" or "failed" or "cancelled" or "blocked" or "finding" or "skipped")) return;
        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            if (!settings.IsDetailedTrackingActive) return;
            var now = DateTimeOffset.UtcNow;
            var directory = Path.Combine(settings.AnalyticsDirectoryPath, "usage");
            Directory.CreateDirectory(directory);
            using var guard = new FileStream(Path.Combine(directory, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var accountId = AnalyticsAccount.Read(dataDirectory);
            var businessId = accountId is null ? null : AnalyticsAccount.Business(dataDirectory);
            var path = Path.Combine(directory, day + (IsCi() ? "-ci" : "-local") + "-" + (accountId ?? "anonymous") + "-" + (businessId ?? "unscoped") + ".json");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) return;
            var state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), json) ?? new() : new State();
            if (feature == "active_minute")
            {
                var minute = now.ToUnixTimeSeconds() / 60;
                if (minute <= state.LastActiveMinute) return; // All localhost tabs share this boundary.
                state.LastActiveMinute = minute;
            }
            var prefix = surface + "_" + feature;
            Add(state, prefix + "_count", count);
            Add(state, prefix + "_" + outcome, count);
            if (double.IsFinite(durationSeconds) && durationSeconds > 0)
            {
                var seconds = Math.Min(durationSeconds, 7 * 86400);
                Add(state, prefix + "_seconds", seconds);
                state.Values[prefix + "_max_seconds"] = Math.Max(state.Values.GetValueOrDefault(prefix + "_max_seconds"), seconds);
            }
            if (hasEvidence) Add(state, prefix + "_with_evidence", 1);
            state.Revision++;
            state.Day = day;
            state.AccountId = accountId;
            state.BusinessId = businessId;
            state.IsCi = IsCi();
            Save(path, state);
        }
        catch { /* Contention, permissions and analytics failures must never affect the product. No waiting. */ }
    }

    public static async Task<T> ObserveAsync<T>(string dataDirectory, string feature, Func<Task<T>> operation, Func<T, string> classify)
    {
        using var actor = AnalyticsAccount.Capture(dataDirectory);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var result = await operation().ConfigureAwait(false);
            try { outcome = classify(result); } catch { outcome = "observed"; }
            return result;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally { Record(dataDirectory, feature, outcome: outcome, durationSeconds: timer.Elapsed.TotalSeconds); }
    }

    public static T Observe<T>(string dataDirectory, string feature, Func<T> operation, Func<T, string> classify)
    {
        using var actor = AnalyticsAccount.Capture(dataDirectory);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var result = operation();
            try { outcome = classify(result); } catch { outcome = "observed"; }
            return result;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally { Record(dataDirectory, feature, outcome: outcome, durationSeconds: timer.Elapsed.TotalSeconds); }
    }

    public static bool IsCi() => new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD" }.Any(key =>
        Environment.GetEnvironmentVariable(key)?.Trim().ToLowerInvariant() is "1" or "true" or "yes")
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BUILD_BUILDID"));

    public static void Flush(string dataDirectory, bool force = false)
    {
        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            var directory = Path.Combine(settings.AnalyticsDirectoryPath, "usage");
            if (!Directory.Exists(directory)) return;
            var now = DateTimeOffset.UtcNow;
            var tick = now.ToUnixTimeSeconds();
            if (!force && tick - lastFlush.GetOrAdd(directory, 0) < 60) return;
            lastFlush[directory] = tick;
            using var guard = new FileStream(Path.Combine(directory, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var enabled = settings.IsDetailedTrackingActive;
            var queued = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p))
            {
                if (!enabled || File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-30)) { File.Delete(path); continue; }
                if (queued >= 64 || new FileInfo(path).Length > 1024 * 1024) continue;
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), json);
                if (state is null || !string.Equals(state.AccountId, AnalyticsAccount.ReadPersisted(dataDirectory), StringComparison.Ordinal))
                {
                    File.Delete(path);
                    continue;
                }
                if (state.Revision <= state.QueuedRevision || !Guid.TryParseExact(state.Epoch, "D", out _)) continue;
                if (!force && state.QueuedRevision > 0 && tick - state.LastQueuedUnixSeconds < 60) continue;
                var identity = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
                var envelope = CreateSnapshotEnvelope(state, identity, now);
                if (envelope is not null && new EventOutbox(settings.AnalyticsDirectoryPath).TryStore(envelope))
                {
                    queued++;
                    state.QueuedRevision = state.Revision;
                    state.LastQueuedUnixSeconds = tick;
                    Save(path, state);
                }
            }
        }
        catch { /* Local queue delivery is best effort, just like existing detailed events. */ }
    }

    internal static EventEnvelope? CreateSnapshotEnvelope(State state, string identity, DateTimeOffset timestampUtc)
    {
        if (state.Revision <= 0
            || !Guid.TryParse(state.AccountId, out _)
            || !Guid.TryParseExact(state.Epoch, "D", out _)
            || !DateOnly.TryParseExact(state.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        var properties = new Dictionary<string, object?>
        {
            ["counter_epoch"] = state.Epoch,
            ["usage_day"] = state.Day,
            ["revision"] = state.Revision,
            ["usage_schema"] = 2,
            ["is_ci"] = state.IsCi,
            ["installation_id"] = identity,
            ["os_platform"] = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other"
        };
        if (Guid.TryParse(state.AccountId, out var account)) properties["account_id"] = account.ToString("D");
        if (Guid.TryParse(state.BusinessId, out var business)) properties["business_id"] = business.ToString("D");
        // Revalidate persisted keys: a damaged/tampered counter file is not an exfiltration channel.
        foreach (var (key, value) in state.Values)
        {
            if (IsMetricKey(key) && double.IsFinite(value) && value >= 0)
            {
                properties[key] = Math.Min(value, 1e12);
            }
        }

        var insertId = CreateSnapshotInsertId(identity, state.Day, state.IsCi, state.Epoch, state.Revision);
        return new EventEnvelope(
            insertId,
            "product_usage_snapshot",
            identity,
            EventLevel.Detailed,
            properties,
            timestampUtc);
    }

    internal static string CreateSnapshotInsertId(
        string identity,
        string day,
        bool isCi,
        string epoch,
        long revision)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"usage:1:{identity}:{day}:{isCi}:{epoch}:{revision}"))).ToLowerInvariant();

    private static bool IsMetricKey(string key) => metricKeys.Contains(key);
    private static void Add(State state, string key, double value) => state.Values[key] = Math.Min(1e12, state.Values.GetValueOrDefault(key) + value);
    private static void Save(string path, State state)
    {
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
                JsonSerializer.Serialize(stream, state, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public sealed class State
    {
        public string? BusinessId { get; set; }
        public string? AccountId { get; set; }
        public string Day { get; set; } = "";
        public string Epoch { get; set; } = Guid.NewGuid().ToString("D");
        public bool IsCi { get; set; }
        public long Revision { get; set; }
        public long QueuedRevision { get; set; }
        public long LastQueuedUnixSeconds { get; set; }
        public long LastActiveMinute { get; set; }
        public Dictionary<string, double> Values { get; set; } = new();
    }
}
