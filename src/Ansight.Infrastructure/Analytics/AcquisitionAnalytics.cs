using System.Security.Cryptography;
using System.Text;

namespace Ansight.Analytics;

/// <summary>Consent-aware acquisition events. Never accepts URLs, credentials or free-form error text.</summary>
public static class AcquisitionAnalytics
{
    private static readonly IReadOnlySet<string> onceEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "cli_install_attributed",
        "cli_first_app_connected",
        "cli_first_session_captured"
    };

    public static string? Journey(string dataDirectory)
    {
        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            if (!settings.IsDetailedTrackingActive) return null;
            var path = Path.Combine(settings.AnalyticsDirectoryPath, "acquisition-id");
            if (File.Exists(path) && Guid.TryParseExact(File.ReadAllText(path), "D", out var saved))
                return saved.ToString("D");
            var candidate = Environment.GetEnvironmentVariable("ANSIGHT_ACQUISITION_ID");
            if (Guid.TryParseExact(candidate, "D", out var supplied))
            {
                Directory.CreateDirectory(settings.AnalyticsDirectoryPath);
                File.WriteAllText(path, supplied.ToString("D"));
                return supplied.ToString("D");
            }
            return null;
        }
        catch { return null; }
    }

    public static void Track(string dataDirectory, string eventName, string? attemptId = null,
        string? accountId = null, string? outcome = null, bool once = false)
    {
        try
        {
            var settings = new AnalyticsSettingsStore(dataDirectory);
            if (!settings.IsDetailedTrackingActive) return;
            if (eventName is not ("cli_install_attributed" or "cli_update_attributed" or "cli_auth_started"
                or "cli_auth_completed" or "cli_auth_failed" or "cli_first_app_connected" or "cli_first_session_captured")) return;
            var identity = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
            var insertId = once
                ? CreateOnceInsertId(identity, eventName)
                : Guid.NewGuid().ToString("N");
            // A durable marker also prevents re-emitting milestones after the outbox is acknowledged.
            var marker = Path.Combine(settings.AnalyticsDirectoryPath, insertId + ".milestone");
            if (once && File.Exists(marker)) return;
            var properties = new Dictionary<string, object?>
            {
                ["installation_id"] = identity,
                ["journey_id"] = Journey(dataDirectory),
                ["funnel_version"] = 1,
                ["is_ci"] = new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD", "BUILD_BUILDID" }.Any(key => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))),
                ["surface"] = "cli",
            };
            if (Guid.TryParse(Environment.GetEnvironmentVariable("ANSIGHT_INSTALL_ATTEMPT_ID"), out var installerAttempt)) properties["installer_attempt_id"] = installerAttempt.ToString("D");
            if (Guid.TryParse(attemptId, out var attempt)) properties["auth_attempt_id"] = attempt.ToString("D");
            Guid? account = null;
            if (Guid.TryParse(accountId, out var parsedAccount))
            {
                account = parsedAccount;
                properties["account_id"] = parsedAccount.ToString("D");
            }
            else if (Guid.TryParse(AnalyticsAccount.Read(dataDirectory), out var currentAccount))
            {
                account = currentAccount;
                properties["account_id"] = currentAccount.ToString("D");
            }
            if (outcome is "succeeded" or "failed" or "cancelled") properties["outcome"] = outcome;
            var outbox = new EventOutbox(settings.AnalyticsDirectoryPath);
            if (eventName == "cli_auth_completed" && account is not null)
            {
                QueueAccountIdentity(outbox, identity, account.Value, "cli");
            }
            var stored = outbox.TryStore(new EventEnvelope(
                insertId, eventName, identity, EventLevel.Detailed, properties, DateTimeOffset.UtcNow));
            if (once && stored) File.WriteAllText(marker, "1");
        }
        catch { /* Measurement must never affect authentication or capture. */ }
    }

    public static void IdentifyAccount(string dataDirectory, string? accountId, string surface = "cli")
    {
        try
        {
            if (!Guid.TryParse(accountId, out var account)
                || surface is not ("cli" or "host"))
            {
                return;
            }
            var settings = new AnalyticsSettingsStore(dataDirectory);
            if (!settings.IsDetailedTrackingActive)
            {
                return;
            }
            var identity = AnalyticsIdentity.LoadOrCreate(settings.AnalyticsDirectoryPath);
            QueueAccountIdentity(new EventOutbox(settings.AnalyticsDirectoryPath), identity, account, surface);
        }
        catch
        {
            // Account identity measurement must never affect startup or authentication.
        }
    }

    internal static bool IsOnceEvent(string eventName)
        => onceEvents.Contains(eventName);

    internal static string CreateOnceInsertId(string identity, string eventName)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{eventName}"))).ToLowerInvariant();

    internal static EventEnvelope CreateAccountIdentityEnvelope(
        string installationIdentity,
        Guid accountId,
        DateTimeOffset timestampUtc,
        string surface = "cli")
    {
        var accountIdentity = $"account-{accountId:D}";
        var insertId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"account-link:2:{installationIdentity}:{accountIdentity}"))).ToLowerInvariant();
        return new EventEnvelope(
            insertId,
            "cli_account_linked",
            installationIdentity,
            EventLevel.Detailed,
            new Dictionary<string, object?>
            {
                ["installation_id"] = installationIdentity,
                ["account_id"] = accountId.ToString("D"),
                ["surface"] = surface
            },
            timestampUtc);
    }

    private static void QueueAccountIdentity(
        EventOutbox outbox,
        string installationIdentity,
        Guid accountId,
        string surface)
        => outbox.TryStore(CreateAccountIdentityEnvelope(
            installationIdentity,
            accountId,
            DateTimeOffset.UtcNow,
            surface));
}
