namespace Ansight.Infrastructure.Preferences;

public sealed class UserPreferenceChangedEventArgs : EventArgs
{
    public UserPreferenceChangedEventArgs(string key, object? previousValue, object? currentValue)
    {
        Key = key;
        PreviousValue = previousValue;
        CurrentValue = currentValue;
    }

    public string Key { get; }

    public object? PreviousValue { get; }

    public object? CurrentValue { get; }
}
