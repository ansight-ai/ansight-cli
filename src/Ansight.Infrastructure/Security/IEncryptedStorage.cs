namespace Ansight.Infrastructure.Security;

public interface IEncryptedStorage
{
    string? Get(string key);

    void Set(string key, string? value);

    void Remove(string key);

    void Clear();
}
