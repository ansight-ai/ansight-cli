namespace Ansight.Host.Tests.Unit.Security;

public sealed class LinuxSecretServiceEncryptedStorageTests
{
    [Fact]
    public void MigrateLegacyEntriesCopiesCredentialsAndKeepsNewerValues()
    {
        var secrets = new FakeSecretTool();
        secrets.Put("Ansight.Studio", "__ansight_studio_keys", "[\"old\",\"updated\"]");
        secrets.Put("Ansight.Studio", "old", "legacy-value");
        secrets.Put("Ansight.Studio", "updated", "outdated-value");
        secrets.Put("Ansight.Cli", "updated", "current-value");
        var storage = new LinuxSecretServiceEncryptedStorage(secrets.Run);

        storage.MigrateLegacyEntries();
        storage.MigrateLegacyEntries();

        Assert.Equal("legacy-value", storage.Get("old"));
        Assert.Equal("current-value", storage.Get("updated"));
        Assert.Null(secrets.Get("Ansight.Studio", "old"));
        Assert.Null(secrets.Get("Ansight.Studio", "updated"));
        Assert.Null(secrets.Get("Ansight.Studio", "__ansight_studio_keys"));
        Assert.Equal(new[] { "old", "updated" },
            JsonSerializer.Deserialize<string[]>(secrets.Get("Ansight.Cli", "__ansight_cli_keys")!));
    }

    [Fact]
    public void LegacyReadsDoNotWriteAndRemovalCannotResurrectCredentials()
    {
        var secrets = new FakeSecretTool();
        secrets.Put("Ansight.Studio", "__ansight_studio_keys", "[\"token\"]");
        secrets.Put("Ansight.Studio", "token", "legacy-value");
        var storage = new LinuxSecretServiceEncryptedStorage(secrets.Run);

        Assert.Equal("legacy-value", storage.Get("token"));
        Assert.Null(secrets.Get("Ansight.Cli", "token"));
        storage.Set("token", "new-value");
        Assert.Equal("new-value", storage.Get("token"));
        Assert.Null(secrets.Get("Ansight.Studio", "token"));

        storage.Remove("token");
        Assert.Null(storage.Get("token"));
        storage.MigrateLegacyEntries();
        Assert.Null(storage.Get("token"));
    }

    [Fact]
    public void ClearRemovesCredentialsFromBothServices()
    {
        var secrets = new FakeSecretTool();
        secrets.Put("Ansight.Studio", "__ansight_studio_keys", "[\"old\"]");
        secrets.Put("Ansight.Studio", "old", "legacy-value");
        var storage = new LinuxSecretServiceEncryptedStorage(secrets.Run);
        storage.Set("new", "new-value");

        storage.Clear();

        Assert.Empty(secrets.Values);
        Assert.Null(storage.Get("old"));
        Assert.Null(storage.Get("new"));
    }

    [Fact]
    public void MalformedLegacyIndexRemainsIntactWhenMigrationFails()
    {
        var secrets = new FakeSecretTool();
        secrets.Put("Ansight.Studio", "__ansight_studio_keys", "invalid-json");
        secrets.Put("Ansight.Studio", "token", "legacy-value");
        var storage = new LinuxSecretServiceEncryptedStorage(secrets.Run);

        Assert.Throws<InvalidOperationException>(storage.MigrateLegacyEntries);
        Assert.Equal("invalid-json", secrets.Get("Ansight.Studio", "__ansight_studio_keys"));
        Assert.Equal("legacy-value", storage.Get("token"));
    }

    private sealed class FakeSecretTool
    {
        public Dictionary<SecretIdentity, string> Values { get; } = new();

        public void Put(string service, string account, string value) =>
            Values[new SecretIdentity(service, account)] = value;

        public string? Get(string service, string account) =>
            Values.GetValueOrDefault(new SecretIdentity(service, account));

        public string? Run(IReadOnlyList<string> arguments, string? standardInput, bool allowMissing)
        {
            var service = ValueAfter(arguments, "service");
            var account = ValueAfter(arguments, "account");
            return arguments[0] switch
            {
                "lookup" => Get(service, account) is { } value ? value + "\n" : null,
                "store" => Store(service, account, standardInput!),
                "clear" => Clear(service, account),
                _ => throw new InvalidOperationException("Unexpected secret-tool operation.")
            };
        }

        private string? Store(string service, string account, string value)
        {
            Put(service, account, value);
            return string.Empty;
        }

        private string? Clear(string service, string account)
        {
            Values.Remove(new SecretIdentity(service, account));
            return string.Empty;
        }

        private static string ValueAfter(IReadOnlyList<string> arguments, string name)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index] == name)
                    return arguments[index + 1];
            }

            throw new InvalidOperationException($"Missing secret-tool argument '{name}'.");
        }
    }

    private readonly record struct SecretIdentity(string Service, string Account);
}
