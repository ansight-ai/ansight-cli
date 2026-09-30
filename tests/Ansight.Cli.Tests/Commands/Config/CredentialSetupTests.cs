using System.Security.Cryptography;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Tests.Commands.Config;

public sealed class CredentialSetupTests
{
    [Fact]
    public void SetupPersistsReferencesAndFreshRuntimeUsesThemWithoutEnvironment()
    {
        using var directory = TestDirectory.Create();
        var keyPath = Path.Combine(directory.Path, "external.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var data = Path.Combine(directory.Path, "state");
        var arguments = CliArguments.Parse(["config", "credentials", "--key-file", keyPath, "--data-dir", data, "--non-interactive"]);
        Assert.Equal(0, CredentialSetupCommand.Run(arguments, new CliOutput(false, new StringWriter(), new StringWriter()), null, false));
        var saved = new LocalSettingsStore(data).Credentials!;
        Assert.Equal("protected-file", saved.Provider);
        Assert.Equal(keyPath, saved.KeyFile);
        Assert.DoesNotContain(File.ReadAllText(keyPath), File.ReadAllText(new LocalSettingsStore(data).SettingsPath));
        var runtime = CliRuntime.ResolveOptions(CliArguments.Parse(["host", "run", "--data-dir", data]));
        Assert.Equal(keyPath, runtime.SecureStorageKeyFilePath);
        Assert.Equal(keyPath, CliRuntime.CreateHostOptions(runtime).SecureStorageKeyFilePath);
        var before = File.ReadAllText(keyPath);
        Assert.Equal(0, CredentialSetupCommand.Run(CliArguments.Parse(["config", "credentials", "--data-dir", data, "--non-interactive"]), new CliOutput(false, new StringWriter(), new StringWriter()), null, false));
        Assert.Equal(before, File.ReadAllText(keyPath));
    }

    [Fact]
    public void WrongKeyCannotBeSavedOverExistingCiphertext()
    {
        using var directory = TestDirectory.Create();
        var data = Path.Combine(directory.Path, "state");
        var store = Path.Combine(data, "data", "secure-storage.json");
        var original = RandomNumberGenerator.GetBytes(32);
        new FileBackedEncryptedStorage(store, original).Set("test", "retained");
        var keyPath = Path.Combine(directory.Path, "wrong.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var ciphertext = File.ReadAllText(store);
        var arguments = CliArguments.Parse(["config", "credentials", "--key-file", keyPath, "--data-dir", data, "--non-interactive"]);
        Assert.ThrowsAny<Exception>(() => CredentialSetupCommand.Run(arguments, new CliOutput(false, new StringWriter(), new StringWriter()), null, false));
        Assert.Null(new LocalSettingsStore(data).Credentials);
        Assert.Equal(ciphertext, File.ReadAllText(store));
        Assert.False(DoctorCommand.CheckStoreDecryption(store, keyPath).IsSuccess);
        Assert.Equal("retained", FileBackedEncryptedStorage.OpenReadOnly(store, original).Get("test"));
        CryptographicOperations.ZeroMemory(original);
    }

    [Fact]
    public void SetupRefusesKeyReplacementAndSiblingKey()
    {
        using var directory = TestDirectory.Create();
        var data = Path.Combine(directory.Path, "state");
        var settings = new LocalSettingsStore(data);
        settings.SetCredentials(new("protected-file", Path.Combine(directory.Path, "original.key"), null));
        var arguments = CliArguments.Parse(["config", "credentials", "--key-file", Path.Combine(directory.Path, "new.key"), "--data-dir", data]);
        Assert.Throws<CliUsageException>(() => CredentialSetupCommand.Run(arguments, new CliOutput(false), null, false));
        Assert.EndsWith("original.key", new LocalSettingsStore(data).Credentials!.KeyFile);
        var sibling = Path.Combine(directory.Path, "secure-storage.key");
        FileEncryptionKeyProvider.CreateKeyFile(sibling);
        Assert.Throws<CliUsageException>(() => CredentialSetupCommand.ValidateExternalKeyLocation(sibling, Path.Combine(directory.Path, "secure-storage.json")));
    }

    [Fact]
    public void UnsafeKeyPermissionsCannotBeSaved()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var keyPath = Path.Combine(directory.Path, "external.key");
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var data = Path.Combine(directory.Path, "state");
        var arguments = CliArguments.Parse(["config", "credentials", "--key-file", keyPath, "--data-dir", data, "--non-interactive"]);
        Assert.Throws<InvalidOperationException>(() => CredentialSetupCommand.Run(arguments, new CliOutput(false), null, false));
        Assert.Null(new LocalSettingsStore(data).Credentials);
    }
}
