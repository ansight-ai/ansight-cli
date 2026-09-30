namespace Ansight.Infrastructure.Security;

public sealed class InMemoryEncryptedStorage : IEncryptedStorage
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (gate)
        {
            return values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(key));
        }

        lock (gate)
        {
            if (value is null)
            {
                values.Remove(key);
                return;
            }

            values[key] = value;
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (gate)
        {
            values.Remove(key);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            values.Clear();
        }
    }
}
