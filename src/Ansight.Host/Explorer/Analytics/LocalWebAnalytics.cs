using Ansight.Analytics;
using System.Security.Cryptography;
using System.Text;

namespace Ansight.Host.Explorer.Analytics;

internal sealed class LocalWebAnalytics
{
    private readonly AnalyticsSettingsStore settings;
    private readonly ProductAnalytics analytics;
    private readonly EventOutbox outbox;
    private readonly Func<DateTimeOffset> utcNow;

    public LocalWebAnalytics(string baseFolderPath, Func<DateTimeOffset>? utcNow = null)
        : this(new ProductAnalytics(baseFolderPath), utcNow)
    {
    }

    public LocalWebAnalytics(ProductAnalytics analytics, Func<DateTimeOffset>? utcNow = null)
    {
        this.analytics = analytics ?? throw new ArgumentNullException(nameof(analytics));
        settings = new AnalyticsSettingsStore(analytics.DataDirectoryPath);
        outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task TrackAsync(
        LocalWebAnalyticsRequest request,
        bool isExplorer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind == "engagement_action")
        {
            EngagementAnalytics.RecordAction(analytics.DataDirectoryPath, utcNow());
            return Task.CompletedTask;
        }
        var isDaily = kind == "daily_active";
        if (!isDaily && !settings.IsDetailedTrackingActive)
        {
            return Task.CompletedTask;
        }

        if (kind == "usage")
        {
            // Explicit allowlist; do not expose arbitrary host metric names to the browser.
            if (request.Feature is "logs" or "network" or "visual_tree" or "artifacts" or "files" or "playback"
                or "annotations" or "active_minute")
                analytics.RecordUsage(request.Feature, "local_web");
            return Task.CompletedTask;
        }
        var now = utcNow().ToUniversalTime();
        var eventName = kind switch
        {
            "daily_active" => "local_web_daily_active",
            "opened" => "local_web_opened",
            "session_viewed" => "local_web_session_viewed",
            "panel_opened" => "local_web_panel_opened",
            _ => throw new InvalidDataException("Unknown local analytics event kind.")
        };
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["surface"] = "local_web",
            ["tracking_level"] = isDaily ? "daily" : "detailed"
        };
        if (!isDaily) properties["mode"] = isExplorer ? "explorer" : "session";
        if (kind == "session_viewed")
        {
            properties["is_live"] = request.IsLive == true;
            analytics.RecordUsage("session", "local_web");
        }
        else if (kind == "panel_opened")
        {
            var panel = NormalizePanel(request.Panel);
            properties["panel"] = panel;
            analytics.RecordUsage(panel switch
            {
                "apps" => "app", "devices" => "device", "device_location" => "location",
                "test_execution" => "test", _ => panel
            }, "local_web");
        }

        if (!isDaily)
            properties["account_id"] = AnalyticsAccount.Read(analytics.DataDirectoryPath);

        try
        {
            var distinctId = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
            var insertId = isDaily
                ? Hash($"{distinctId}:{eventName}:{now:yyyy-MM-dd}")
                : Guid.NewGuid().ToString("N");
            var envelope = new EventEnvelope(insertId, eventName, distinctId,
                isDaily ? EventLevel.Daily : EventLevel.Detailed, properties, now);
            if (isDaily) EngagementAnalytics.RecordDailySignal(analytics.DataDirectoryPath, envelope);
            else outbox.TryStore(envelope);
        }
        catch
        {
            // Local analytics must never affect the explorer.
        }

        return Task.CompletedTask;
    }

    private static string NormalizePanel(string? panel)
        => panel?.Trim().ToLowerInvariant() switch
        {
            "apps" => "apps",
            "device_location" => "device_location",
            "trends" => "trends",
            "about" => "about", "account" => "account", "app_graph" => "app_graph",
            "devices" => "devices", "health" => "health", "session_admin" => "session_admin",
            "settings" => "settings", "test_execution" => "test_execution", "test_history" => "test_history",
            _ => throw new InvalidDataException("Unknown local analytics panel.")
        };

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes[..16]).ToLowerInvariant();
    }
}
