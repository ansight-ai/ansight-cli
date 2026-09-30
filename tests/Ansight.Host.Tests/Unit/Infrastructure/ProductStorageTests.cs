using System.Text.Json;
using Ansight.Infrastructure.Preferences;
using Ansight.Infrastructure.Security;
using Ansight.Infrastructure.Teams;
using Ansight.Infrastructure.Theming;

namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class ProductStorageTests
{

    [Fact]
    public void KnownTeamsUseCurrentStorageKey()
    {
        const string key = "ansight.studio.known_teams.profiles";
        var storage = new InMemoryEncryptedStorage();
        var original = new KnownTeamProfile("user-1", Guid.NewGuid(), "Original team");
        storage.Set(key, JsonSerializer.Serialize(new[] { original }));
        var current = new EncryptedKnownTeamStore(storage);

        Assert.Equal(original, current.Get(original.UserId));
        var updated = original with { Name = "Updated team" };
        current.Save(updated);
        Assert.Equal(updated, Assert.Single(JsonSerializer.Deserialize<KnownTeamProfile[]>(storage.Get(key)!)!));
        current.Remove(original.UserId);
        Assert.Null(storage.Get(key));
    }

    [Fact]
    public void PreferencesAndThemesRoundTripThroughCurrentFileFormat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ansight-preference-compatibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "studio-preferences.json");
            File.WriteAllText(path, """
                {"ansight.studio.preferences.adb_path":"/saved/adb",
                 "ansight.studio.preferences.theme":"ansight.studio.theme.light"}
                """);
            var current = new FilePreferencesStore(path);
            Assert.Equal("/saved/adb", current.Get(PreferenceKeys.AdbPath, string.Empty));
            Assert.Equal(ThemeIdentifiers.Light, current.Get(PreferenceKeys.PreferredTheme, string.Empty));

            current.Set(PreferenceKeys.AdbPath, "/updated/adb");
            current.Set(PreferenceKeys.PreferredTheme, ThemeIdentifiers.Dark);
            var olderReader = new FilePreferencesStore(path);
            Assert.Equal("/updated/adb", olderReader.Get("ansight.studio.preferences.adb_path", string.Empty));
            Assert.Equal("ansight.studio.theme.dark", olderReader.Get("ansight.studio.preferences.theme", string.Empty));
            olderReader.Set("ansight.studio.preferences.adb_path", "/older-host/adb");
            Assert.Equal("/older-host/adb", new FilePreferencesStore(path).Get(PreferenceKeys.AdbPath, string.Empty));

            olderReader.Remove("ansight.studio.preferences.adb_path");
            Assert.False(new FilePreferencesStore(path).Contains(PreferenceKeys.AdbPath));
            Assert.False(File.Exists(Path.Combine(root, "preferences.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
