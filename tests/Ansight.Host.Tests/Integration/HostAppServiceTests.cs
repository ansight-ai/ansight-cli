using Ansight.Host.Tests.TestSupport;
using Ansight.Host;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Integration;

public sealed class HostAppServiceTests
{
    [Fact]
    public void RegisterListGetAndRemoveUsePublicHostService()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);

        var registered = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.cli",
            "CLI fixture",
            directory.Path));

        Assert.True(registered.IsSuccess);
        Assert.Equal("CLI fixture", runtime.Apps.Get("com.example.cli")?.Name);
        Assert.True(runtime.Apps.Get("com.example.cli")?.RepositoryAutomationsEnabled);
        Assert.Contains(runtime.Apps.List(), app => app.AppId == "com.example.cli");

        var removed = runtime.Apps.Remove("com.example.cli");

        Assert.True(removed.IsSuccess);
        Assert.Null(runtime.Apps.Get("com.example.cli"));
    }

    [Fact]
    public void RegisterWithCodebaseReenablesRepositoryAutomations()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);
        var registered = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.cli",
            "CLI fixture",
            directory.Path));
        Assert.True(registered.IsSuccess);

        var app = Assert.Single(runtime.Apps.GetDefinitions());
        app.RepositoryAutomationsEnabled = false;
        runtime.Apps.SaveDefinition(app);

        var relinked = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.cli",
            null,
            directory.Path));

        Assert.True(relinked.IsSuccess);
        Assert.True(relinked.App?.RepositoryAutomationsEnabled);
    }

    [Fact]
    public void PairingAndProfilingFacadesAreAvailableWithoutStartingUi()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);

        var invalidInvite = runtime.Pairing.Issue(duration: "not-a-duration");
        var captures = runtime.Profiling.ListCaptures();
        var nativeCaptures = runtime.NativeProfiling.ListCaptures();

        Assert.False(invalidInvite.IsSuccess);
        Assert.Empty(runtime.Pairing.List());
        Assert.Empty(captures);
        Assert.Empty(nativeCaptures);
    }

    [Fact]
    public void PairingFacadeCreatesOwnerOnlyPhoneEnrollmentQr()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);
        var issue = runtime.Pairing.Issue("com.example.cli", "CLI fixture", "10m");
        var qrPath = Path.Combine(directory.Path, "enrollment.png");

        try
        {
            var qr = runtime.Pairing.CreateQr(
                issue.Invite!.InviteId,
                qrPath,
                "192.0.2.10");

            Assert.True(qr.IsSuccess, qr.Message);
            Assert.Equal(qrPath, qr.QrFilePath);
            Assert.StartsWith("ans2:", qr.PairingCode, StringComparison.Ordinal);
            Assert.Contains("192.0.2.10", qr.HostAddresses);
            Assert.Equal(
                [0x89, 0x50, 0x4e, 0x47],
                File.ReadAllBytes(qrPath).Take(4).Select(static value => (int)value).ToArray());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(qrPath));
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(issue.InviteFilePath))
            {
                File.Delete(issue.InviteFilePath);
            }
        }
    }

    [Fact]
    public void PairingFacadeCreatesManualEnrollmentCode()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);
        var issue = runtime.Pairing.Issue("com.example.cli", "CLI fixture", "10m");

        try
        {
            var code = runtime.Pairing.CreateCode(issue.Invite!.InviteId, "192.0.2.10");

            Assert.True(code.IsSuccess, code.Message);
            Assert.StartsWith("ans2:", code.PairingCode, StringComparison.Ordinal);
            Assert.Contains("192.0.2.10", code.HostAddresses);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(issue.InviteFilePath))
            {
                File.Delete(issue.InviteFilePath);
            }
        }
    }

    [Fact]
    public void ConfigureRepositoryWorkspaceUpdatesAnAppDiscoveredFromAConnectedSession()
    {
        using var directory = TestDirectory.Create();
        using var runtime = CreateRuntime(directory.Path);
        var registered = runtime.Apps.Register(new AppRegistrationRequest(
            "com.example.cli",
            "CLI fixture"));
        Assert.True(registered.IsSuccess);

        var configured = runtime.Apps.ConfigureRepositoryWorkspace(
            "com.example.cli",
            directory.Path);

        Assert.True(configured.IsSuccess);
        Assert.Equal(directory.Path, configured.App?.CodebasePath);
        Assert.True(configured.App?.RepositoryAutomationsEnabled);
    }

    private static RuntimeCoordinator CreateRuntime(string baseFolderPath)
    {
        var storagePath = Path.Combine(baseFolderPath, "secrets.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = baseFolderPath,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
        });
    }
}
