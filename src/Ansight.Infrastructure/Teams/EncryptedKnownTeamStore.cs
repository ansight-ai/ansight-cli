using Ansight.Infrastructure.Security;

namespace Ansight.Infrastructure.Teams;

public sealed class EncryptedKnownTeamStore : IKnownTeamStore
{
    private const string ProfilesKey = "ansight.studio.known_teams.profiles";
    private readonly Lock gate = new();
    private readonly IEncryptedStorage encryptedStorage;

    public EncryptedKnownTeamStore(IEncryptedStorage encryptedStorage)
    {
        this.encryptedStorage = encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage));
    }

    public KnownTeamProfile? Get(string userId)
    {
        var normalizedUserId = Normalize(userId);
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return null;
        }

        lock (gate)
        {
            return LoadProfiles().FirstOrDefault(profile =>
                string.Equals(profile.UserId, normalizedUserId, StringComparison.Ordinal));
        }
    }

    public void Save(KnownTeamProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalizedUserId = Normalize(profile.UserId);
        var normalizedName = Normalize(profile.Name);
        if (string.IsNullOrWhiteSpace(normalizedUserId)
            || profile.TeamId == Guid.Empty
            || string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new ArgumentException("Known team details are incomplete.", nameof(profile));
        }

        lock (gate)
        {
            var profiles = LoadProfiles();
            profiles.RemoveAll(candidate =>
                string.Equals(candidate.UserId, normalizedUserId, StringComparison.Ordinal));
            profiles.Add(new KnownTeamProfile(normalizedUserId, profile.TeamId, normalizedName));
            Persist(profiles);
        }
    }

    public void Remove(string userId)
    {
        var normalizedUserId = Normalize(userId);
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return;
        }

        lock (gate)
        {
            var profiles = LoadProfiles();
            if (profiles.RemoveAll(profile =>
                    string.Equals(profile.UserId, normalizedUserId, StringComparison.Ordinal)) > 0)
            {
                Persist(profiles);
            }
        }
    }

    private List<KnownTeamProfile> LoadProfiles()
    {
        var json = encryptedStorage.Get(ProfilesKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<KnownTeamProfile>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist(IReadOnlyCollection<KnownTeamProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            encryptedStorage.Remove(ProfilesKey);
            return;
        }

        encryptedStorage.Set(ProfilesKey, JsonSerializer.Serialize(profiles));
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
