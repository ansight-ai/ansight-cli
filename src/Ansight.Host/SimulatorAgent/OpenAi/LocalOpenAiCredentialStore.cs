using Ansight.Infrastructure.Security;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal sealed class LocalOpenAiCredentialStore
{
    private const string StorageKey = "runner.openai-api-key.v1";
    private readonly IEncryptedStorage encryptedStorage;

    public LocalOpenAiCredentialStore(IEncryptedStorage encryptedStorage)
    {
        this.encryptedStorage = encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Read());

    internal string? Read()
    {
        return encryptedStorage.Get(StorageKey);
    }

    public void Save(string? apiKey)
    {
        var key = apiKey?.Trim();
        var isInvalid = string.IsNullOrWhiteSpace(key)
            || key.Length > 4096
            || key.Any(char.IsWhiteSpace)
            || key.Any(char.IsControl);

        if (isInvalid)
        {
            throw new ArgumentException("Enter a valid OpenAI API key without spaces or line breaks.");
        }

        encryptedStorage.Set(StorageKey, key);
    }

    public void Remove()
    {
        encryptedStorage.Remove(StorageKey);
    }
}
