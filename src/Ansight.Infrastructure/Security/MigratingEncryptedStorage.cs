namespace Ansight.Infrastructure.Security;

public sealed class MigratingEncryptedStorage : IEncryptedStorage
{
    private readonly IEncryptedStorage fallback;
    private readonly IEncryptedStorage primary;

    public MigratingEncryptedStorage(IEncryptedStorage primary, IEncryptedStorage fallback)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public string? Get(string key)
    {
        var value = primary.Get(key);
        if (value is not null)
        {
            return value;
        }

        value = fallback.Get(key);
        if (value is not null)
        {
            primary.Set(key, value);
            fallback.Remove(key);
        }

        return value;
    }

    public void Set(string key, string? value)
    {
        if (value is null)
        {
            Remove(key);
            return;
        }

        primary.Set(key, value);
        fallback.Remove(key);
    }

    public void Remove(string key)
    {
        try
        {
            fallback.Remove(key);
        }
        finally
        {
            primary.Remove(key);
        }
    }

    public void Clear()
    {
        try
        {
            fallback.Clear();
        }
        finally
        {
            primary.Clear();
        }
    }
}
