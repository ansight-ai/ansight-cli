using System.Collections.Concurrent;
using System.Text.Json;

namespace Ansight.Analytics;

/// <summary>Verified organisation memberships plus explicitly selected capture context.</summary>
public static class CaptureBusinessAttribution
{
    private static readonly ConcurrentDictionary<Guid, Selection> selections = new();
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public static IDisposable Select(string dataDirectory, string appId, Guid? businessId)
    {
        var key = Guid.NewGuid();
        selections[key] = new(Path.GetFullPath(dataDirectory), appId, AnalyticsAccount.Read(dataDirectory), businessId);
        return new SelectionScope(key);
    }

    public static string? Resolve(string dataDirectory, string appId, string? appBusinessId)
    {
        try
        {
            var account = AnalyticsAccount.Read(dataDirectory);
            if (account is null)
            {
                return null;
            }

            var root = Path.GetFullPath(dataDirectory);
            var active = selections.Values
                .Where(selection =>
                {
                    var matchesCaptureContext = selection.Directory == root
                        && selection.AppId == appId
                        && selection.AccountId == account
                        && selection.BusinessId.HasValue;

                    return matchesCaptureContext;
                })
                .Select(selection => selection.BusinessId!.Value.ToString("D"))
                .Distinct()
                .ToArray();

            // Concurrent runs for different organisations cannot claim an unsolicited SDK connection.
            if (active.Length > 1)
            {
                return null;
            }

            var requested = active.SingleOrDefault()
                ?? AnalyticsAccount.Business(dataDirectory)
                ?? appBusinessId
                ?? Environment.GetEnvironmentVariable("ANSIGHT_TEAM_ID");

            if (!Guid.TryParse(requested, out var business))
            {
                return null;
            }

            var path = Path.Combine(root, "analytics", "capture-businesses.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<MembershipCache>(File.ReadAllText(path), json);
            if (cache?.AccountId != account || cache.CheckedUtc < DateTimeOffset.UtcNow.AddDays(-7))
            {
                return null;
            }

            var hasVerifiedMembership = cache.Businesses.Any(member =>
            {
                if (member.Id != business)
                {
                    return false;
                }

                var allowsApp = member.AppIds.Length == 0
                    || member.AppIds.Contains("*")
                    || member.AppIds.Contains(appId, StringComparer.OrdinalIgnoreCase);

                return allowsApp;
            });

            return hasVerifiedMembership ? business.ToString("D") : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveVerifiedMemberships(string dataDirectory, string accountId, JsonElement teams)
    {
        try
        {
            if (teams.ValueKind != JsonValueKind.Array || AnalyticsAccount.ReadPersisted(dataDirectory) != accountId)
            {
                return;
            }

            var businesses = new List<Business>();
            foreach (var team in teams.EnumerateArray())
            {
                var isOrganisation = team.TryGetProperty("workspace_kind", out var kind)
                    && kind.GetString() == "organisation";

                if (!isOrganisation)
                {
                    continue;
                }

                if (!team.TryGetProperty("id", out var id) || !id.TryGetGuid(out var business))
                {
                    continue;
                }

                var hasAppPolicy = team.TryGetProperty("allowed_app_ids", out var allowed)
                    && allowed.ValueKind == JsonValueKind.Array;

                var apps = hasAppPolicy
                    ? allowed.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString()!)
                        .ToArray()
                    : ["*"];

                businesses.Add(new(business, apps));
            }

            LocalCaptureUsage.WriteAtomic(
                Path.Combine(dataDirectory, "analytics", "capture-businesses.json"),
                new MembershipCache(accountId, DateTimeOffset.UtcNow, businesses.ToArray()));
        }
        catch
        {
            // Membership refresh is best effort and never changes past attribution.
        }
    }

    public static bool NeedsRefresh(string dataDirectory, string accountId)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "analytics", "capture-businesses.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            {
                return true;
            }

            var cache = JsonSerializer.Deserialize<MembershipCache>(File.ReadAllText(path), json);
            return cache?.AccountId != accountId || cache.CheckedUtc < DateTimeOffset.UtcNow.AddMinutes(-15);
        }
        catch
        {
            return true;
        }
    }

    private sealed record MembershipCache(string AccountId, DateTimeOffset CheckedUtc, Business[] Businesses);

    private sealed record Business(Guid Id, string[] AppIds);

    private sealed record Selection(string Directory, string AppId, string? AccountId, Guid? BusinessId);

    private sealed class SelectionScope : IDisposable
    {
        private readonly Guid key;

        public SelectionScope(Guid key)
        {
            this.key = key;
        }

        public void Dispose()
        {
            selections.TryRemove(key, out _);
        }
    }
}
