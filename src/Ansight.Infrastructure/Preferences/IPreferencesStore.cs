namespace Ansight.Infrastructure.Preferences;

public interface IPreferencesStore
{
    event EventHandler<UserPreferenceChangedEventArgs>? Changed;

    bool Contains(string key);

    T Get<T>(string key, T defaultValue);

    void Set<T>(string key, T value);

    bool Remove(string key);
}
