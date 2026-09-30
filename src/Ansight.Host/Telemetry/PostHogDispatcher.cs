using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Ansight.Analytics;

namespace Ansight.Host.Telemetry;

public sealed class PostHogDispatcher
{
    private const int BatchSize = 100;
    private static readonly HttpClient sharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ProductAnalytics analytics;
    private readonly AnalyticsSettingsStore settings;
    private readonly EventOutbox outbox;
    private readonly LegacyAnalyticsMigration legacyMigration;
    private readonly HttpClient httpClient;
    private readonly string apiKey;
    private readonly string batchUrl;

    public PostHogDispatcher(string dataDirectory)
        : this(dataDirectory, sharedHttpClient, "https://us.i.posthog.com", PostHogBuildConfiguration.ProjectToken)
    {
    }

    internal PostHogDispatcher(string dataDirectory, HttpClient httpClient, string apiHost, string apiKey)
    {
        analytics = new ProductAnalytics(dataDirectory);
        settings = new AnalyticsSettingsStore(dataDirectory);
        outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        legacyMigration = new LegacyAnalyticsMigration(dataDirectory);
        this.httpClient = httpClient;
        this.apiKey = apiKey.Trim();
        batchUrl = $"{apiHost.TrimEnd('/')}/batch/";
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (apiKey.Length == 0) return;
        var retrySeconds = 5;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delivered = await FlushAsync(cancellationToken).ConfigureAwait(false);
                retrySeconds = delivered ? 5 : Math.Min(retrySeconds * 2, 60);
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Unacknowledged events remain on disk for the next host run.
        }
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        if (apiKey.Length == 0) return false;
        try
        {
            legacyMigration.Migrate();
            analytics.Flush();
            if (!settings.IsDetailedTrackingActive) outbox.RemoveDetailedEvents();
            var pending = outbox.Load(BatchSize);
            var activeAccount = AnalyticsAccount.ReadPersisted(analytics.DataDirectoryPath);
            foreach (var item in pending.Where(item => item.Envelope.Level == EventLevel.Detailed
                && (!item.Envelope.Properties.TryGetValue("account_id", out var account)
                    || !string.Equals(account?.ToString(), activeAccount, StringComparison.Ordinal))))
                outbox.Remove(item.Path);
            pending = pending.Where(item => item.Envelope.Level != EventLevel.Detailed
                || item.Envelope.Properties.TryGetValue("account_id", out var account)
                && string.Equals(account?.ToString(), activeAccount, StringComparison.Ordinal)).ToArray();
            if (pending.Count == 0) return true;

            // Old clients queued permanent installation/person aliases. Discard these
            // obsolete identity mutations; auth events and explicit account links remain.
            foreach (var item in pending.Where(item => item.Envelope.EventName == "$identify")) outbox.Remove(item.Path);
            var batch = pending.Where(item => item.Envelope.EventName != "$identify").Select(item => new Dictionary<string, object?>
            {
                ["event"] = item.Envelope.EventName,
                ["distinct_id"] = item.Envelope.DistinctId,
                ["timestamp"] = item.Envelope.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                ["properties"] = new Dictionary<string, object?>(item.Envelope.Properties)
                {
                    ["$insert_id"] = item.Envelope.InsertId,
                    ["$process_person_profile"] = false
                }
            }).ToArray();
            if (batch.Length == 0) return true;
            using var response = await httpClient.PostAsJsonAsync(batchUrl,
                new { api_key = apiKey, batch }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!result.RootElement.TryGetProperty("status", out var status)
                || !(status.ValueKind == JsonValueKind.String && status.GetString() == "Ok"
                    || status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var value) && value == 1))
                return false;

            foreach (var item in pending) outbox.Remove(item.Path);
            return true;
        }
        catch (Exception)
        {
            // Network, storage and malformed responses must never stop the host.
            return false;
        }
    }
}
