using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ansight.Infrastructure.Analytics;

/// <summary>Local-only capture receipts, aggregated into versioned daily snapshots.</summary>
public sealed class LocalCaptureUsage
{
    public const string EventName = "local_capture_usage_snapshot";
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, long> lastFlush = new();
    private readonly string dataDirectory;
    private readonly string root;

    public LocalCaptureUsage(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        root = Path.Combine(Path.GetFullPath(dataDirectory), "analytics", "local-captures");
    }

    public static string CaptureKey(string sessionId, DateTimeOffset createdUtc)
    {
        return Hash($"{sessionId}\n{createdUtc.UtcTicks}");
    }

    // Called only by the two local creation paths, never by archive import or replay.
    public bool Register(string sessionId, DateTimeOffset createdUtc, string source, string? businessId = null)
    {
        if (source is not ("sdk" or "device"))
        {
            return false;
        }

        return Mutate(state =>
        {
            state.Captures.TryAdd(CaptureKey(sessionId, createdUtc), new Capture
            {
                StartedUtc = createdUtc,
                Source = source,
                BusinessId = Guid.TryParse(businessId, out var id) ? id.ToString("D") : null,
                IsCi = ProductUsage.IsCi()
            });
        });
    }

    public bool IsRegistered(string sessionId, DateTimeOffset createdUtc)
    {
        return RegisteredCaptureKeys().Contains(CaptureKey(sessionId, createdUtc));
    }

    public IReadOnlySet<string> RegisteredCaptureKeys()
    {
        try
        {
            var state = Read<State>(Path.Combine(root, "state.json"));

            if (state is null)
            {
                return new HashSet<string>();
            }

            var matchesCurrentConsent = state.AccountId == AnalyticsAccount.ReadPersisted(dataDirectory)
                && state.ConsentId == new AnalyticsSettingsStore(dataDirectory).DetailedTrackingConsentId;

            return matchesCurrentConsent
                ? state.Captures.Keys.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    /// <summary>Queue a durable receipt after evidence has been saved. Repeated receipts are harmless.</summary>
    public bool RecordSavedEvidence(string sessionId, DateTimeOffset createdUtc, DateTimeOffset savedUtc)
    {
        try
        {
            if (!new AnalyticsSettingsStore(dataDirectory).IsDetailedTrackingActive)
            {
                return true;
            }

            var key = CaptureKey(sessionId, createdUtc);
            var path = Path.Combine(root, "pending", key + ".json");

            if (!File.Exists(path))
            {
                WriteAtomic(path, new Receipt(key, savedUtc), overwrite: false);
            }

            return true;
        }
        catch
        {
            // The session summary retains the receipt time for recovery.
            return false;
        }
    }

    public void Flush(bool force = false)
    {
        try
        {
            // A stale operation scope must not clear the current account's ledger.
            // Explicit opt-out and account changes clear it at their settings boundary.
            if (!new AnalyticsSettingsStore(dataDirectory).IsDetailedTrackingActive)
            {
                return;
            }

            var tick = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!force && tick - lastFlush.GetOrAdd(root, 0) < 60)
            {
                return;
            }

            lastFlush[root] = tick;

            var envelopes = new List<EventEnvelope>();
            string? snapshotConsent = null;

            Mutate(state =>
            {
                snapshotConsent = state.ConsentId;

                var now = DateTimeOffset.UtcNow;
                var oldestDay = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-29), TimeSpan.Zero);
                var pending = Path.Combine(root, "pending");
                var paths = Directory.Exists(pending) ? Directory.GetFiles(pending, "*.json") : [];
                var consumed = new List<string>();

                foreach (var path in paths)
                {
                    try
                    {
                        var receipt = Read<Receipt>(path);
                        ApplyReceipt(state, receipt, now);
                        consumed.Add(path);
                    }
                    catch (JsonException)
                    {
                        consumed.Add(path);
                    }
                    catch (IOException)
                    {
                        // Retry an unreadable receipt without blocking other captures.
                    }
                }

                // Remove whole expired UTC days, never part of a cumulative daily counter.
                var expiredCaptureKeys = state.Captures
                    .Where(pair => (pair.Value.FirstEvidenceUtc ?? pair.Value.StartedUtc) < oldestDay)
                    .Select(pair => pair.Key)
                    .ToArray();

                foreach (var key in expiredCaptureKeys)
                {
                    state.Captures.Remove(key);
                }

                var expiredAcknowledgements = state.Acknowledged
                    .Where(pair => pair.Value < oldestDay)
                    .Select(pair => pair.Key)
                    .ToArray();

                foreach (var id in expiredAcknowledgements)
                {
                    state.Acknowledged.Remove(id);
                }

                // Receipt consumption and deduplication are one atomic ledger write.
                WriteAtomic(Path.Combine(root, "state.json"), state);

                foreach (var path in consumed)
                {
                    File.Delete(path);
                }

                var installation = AnalyticsIdentity.LoadOrCreate(Path.GetDirectoryName(root)!);
                var dailyCaptures = state.Captures.Values
                    .Where(capture => HasReportableEvidence(capture, now))
                    .GroupBy(capture => new DailyKey(
                        capture.FirstEvidenceUtc!.Value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        capture.BusinessId,
                        capture.IsCi));

                foreach (var group in dailyCaptures)
                {
                    var sdk = group.Count(capture => capture.Source == "sdk");
                    var device = group.Count(capture => capture.Source == "device");
                    var count = sdk + device;
                    var insertId = Hash($"local-captures:1:{installation}:{state.Epoch}:{group.Key.Day}:{group.Key.BusinessId}:{group.Key.IsCi}:{count}");

                    if (state.Acknowledged.ContainsKey(insertId))
                    {
                        continue;
                    }

                    var properties = new Dictionary<string, object?>
                    {
                        ["capture_schema"] = 1,
                        ["counter_epoch"] = state.Epoch,
                        ["consent_epoch"] = state.ConsentId,
                        ["account_id"] = state.AccountId,
                        ["business_id"] = group.Key.BusinessId,
                        ["installation_id"] = installation,
                        ["usage_day"] = group.Key.Day,
                        ["is_ci"] = group.Key.IsCi,
                        ["revision"] = count,
                        ["capture_count"] = count,
                        ["sdk_count"] = sdk,
                        ["device_count"] = device
                    };

                    envelopes.Add(new EventEnvelope(
                        insertId,
                        EventName,
                        installation,
                        EventLevel.Detailed,
                        properties,
                        group.Max(capture => capture.FirstEvidenceUtc!.Value)));
                }
            });

            var settings = new AnalyticsSettingsStore(dataDirectory);

            if (snapshotConsent is null || settings.DetailedTrackingConsentId != snapshotConsent)
            {
                return;
            }

            var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);

            foreach (var envelope in envelopes)
            {
                if (envelope.Properties["account_id"] as string != AnalyticsAccount.ReadPersisted(dataDirectory))
                {
                    break;
                }

                outbox.TryStore(envelope);
            }

            // Requeue unacknowledged snapshots, including after outbox eviction.
            // Their stable IDs and revision-based reporting make delivery retries idempotent.
        }
        catch
        {
            // Analytics must not interrupt capture or offline operation.
        }
    }

    private static void ApplyReceipt(State state, Receipt? receipt, DateTimeOffset now)
    {
        if (receipt is null || string.IsNullOrEmpty(receipt.Key))
        {
            return;
        }

        if (!state.Captures.TryGetValue(receipt.Key, out var capture))
        {
            return;
        }

        var canRecordFirstEvidence = capture.FirstEvidenceUtc is null
            && receipt.SavedUtc >= capture.StartedUtc
            && receipt.SavedUtc <= now.AddMinutes(5);

        if (canRecordFirstEvidence)
        {
            capture.FirstEvidenceUtc = receipt.SavedUtc;
        }
    }

    private static bool HasReportableEvidence(Capture capture, DateTimeOffset now)
    {
        var hasValidEvidenceTime = capture.FirstEvidenceUtc is not null
            && capture.FirstEvidenceUtc >= capture.StartedUtc
            && capture.FirstEvidenceUtc <= now.AddMinutes(5);

        if (!hasValidEvidenceTime)
        {
            return false;
        }

        var hasSupportedSource = capture.Source is "sdk" or "device";
        var hasValidBusiness = capture.BusinessId is null || Guid.TryParse(capture.BusinessId, out _);

        return hasSupportedSource && hasValidBusiness;
    }

    public void Acknowledge(IEnumerable<EventEnvelope> delivered)
    {
        var captures = delivered.Where(envelope => envelope.EventName == EventName).ToArray();

        if (captures.Length == 0)
        {
            return;
        }

        Mutate(state =>
        {
            foreach (var envelope in captures)
            {
                var matchesCurrentLedger = envelope.Properties.TryGetValue("counter_epoch", out var epoch)
                    && epoch?.ToString() == state.Epoch
                    && envelope.Properties.TryGetValue("account_id", out var account)
                    && account?.ToString() == state.AccountId
                    && envelope.Properties.TryGetValue("consent_epoch", out var consent)
                    && consent?.ToString() == state.ConsentId;

                if (matchesCurrentLedger)
                {
                    state.Acknowledged[envelope.InsertId] = envelope.TimestampUtc;
                }
            }
        });
    }

    public void Clear()
    {
        try
        {
            using var guard = AcquireLock();

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            lastFlush.TryRemove(root, out _);
        }
        catch
        {
            // A new consent epoch also prevents old receipts from being reused.
        }
    }

    private bool Mutate(Action<State> change)
    {
        try
        {
            using var guard = AcquireLock();
            var settings = new AnalyticsSettingsStore(dataDirectory);
            var consent = settings.DetailedTrackingConsentId;
            var account = AnalyticsAccount.ReadPersisted(dataDirectory);
            var hasValidConsent = consent is not null
                && (consent == "legacy"
                    || Guid.TryParse(consent, out _));

            if (!hasValidConsent || !Guid.TryParse(account, out _))
            {
                return false;
            }

            var path = Path.Combine(root, "state.json");
            var state = Read<State>(path);
            var needsNewLedger = state is null
                || state.AccountId != account
                || state.ConsentId != consent
                || !Guid.TryParse(state.Epoch, out _);

            if (needsNewLedger)
            {
                state = new State
                {
                    AccountId = account!,
                    ConsentId = consent!
                };
            }

            change(state!);
            WriteAtomic(path, state);

            return true;
        }
        catch
        {
            // Failed evidence updates remain in the pending receipt queue.
            return false;
        }
    }

    private IDisposable AcquireLock()
    {
        var mutex = new Mutex(false, "ansight-capture-usage-" + Hash(root));

        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(2)))
                {
                    throw new IOException("Capture usage is busy.");
                }
            }
            catch (AbandonedMutexException)
            {
                // The abandoned mutex is now owned by this thread.
            }

            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        if (new FileInfo(path).Length > 32 * 1024 * 1024)
        {
            throw new IOException("Capture usage ledger exceeds its limit.");
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), json);
    }

    internal static void WriteAtomic<T>(string path, T value, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, value, json);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed class MutexLease : IDisposable
    {
        private readonly Mutex mutex;

        public MutexLease(Mutex mutex)
        {
            this.mutex = mutex;
        }

        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private sealed record Receipt(string Key, DateTimeOffset SavedUtc);

    private sealed record DailyKey(string Day, string? BusinessId, bool IsCi);

    private sealed class State
    {
        public string Epoch { get; set; } = Guid.NewGuid().ToString("D");
        public string AccountId { get; set; } = "";
        public string ConsentId { get; set; } = "";
        public Dictionary<string, Capture> Captures { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Acknowledged { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Capture
    {
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? FirstEvidenceUtc { get; set; }
        public string Source { get; set; } = "";
        public string? BusinessId { get; set; }
        public bool IsCi { get; set; }
    }
}
