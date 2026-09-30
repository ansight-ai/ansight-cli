namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class PairingConfigServiceTests
{
    [Fact]
    public void Issue_WritesCurrentEnrollmentInviteAndSeedsCache()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var identityStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var pairingCache = new PairingConfigCache(
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var service = new PairingConfigService(
            environment.ApplicationPaths,
            identityStore,
            pairingCache);

        string? inviteFilePath = null;
        try
        {
            var result = service.Issue(
                "Example Test App",
                "com.example.testapp",
                PairingConfigDuration.Default);
            inviteFilePath = result.DesktopPath;

            Assert.True(File.Exists(inviteFilePath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(inviteFilePath));
            }

            var json = File.ReadAllText(inviteFilePath);
            var invite = Assert.IsType<PairingConfig>(PublicPairingConfigJson.TryDeserialize(json));

            Assert.Equal(PairingConfig.SchemaName, invite.Schema);
            Assert.Equal(result.ConfigId, invite.ConfigId);
            Assert.Equal("Example Test App", invite.AppName);
            Assert.Equal("com.example.testapp", invite.AppId);
            Assert.Equal([PairingTransportNames.Ws], invite.AllowedTransports);
            Assert.Equal(32, CryptoUtil.FromBase64Url(invite.Enrollment!.Secret).Length);
            Assert.Equal(1, invite.Enrollment.MaxUses);
            Assert.Equal("read", invite.Enrollment.MaxToolPolicy);
            Assert.Equal(result.ExpiresAt, invite.ExpiresAt);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("inviteId", out _));
            Assert.True(root.GetProperty("enrollment").TryGetProperty("accessToken", out _));
            Assert.False(root.TryGetProperty("signature", out _));
            Assert.False(root.GetProperty("host").TryGetProperty("tlsPins", out _));

            var cachedInvite = Assert.IsType<CachedPairingConfig>(pairingCache.Find(invite.ConfigId));
            Assert.Equal(invite.Enrollment.Secret, cachedInvite.Config.Enrollment?.Secret);
            Assert.False(cachedInvite.Consumed);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(inviteFilePath) && File.Exists(inviteFilePath))
            {
                File.Delete(inviteFilePath);
            }
        }
    }

    [Fact]
    public void Issue_CreatesIndependentOneUseInvites()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var identityStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var cache = new PairingConfigCache(
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var service = new PairingConfigService(environment.ApplicationPaths, identityStore, cache);
        PairingIssueResult? first = null;
        PairingIssueResult? second = null;

        try
        {
            first = service.Issue("Example", "com.example.app", PairingConfigDuration.Default);
            second = service.Issue("Example", "com.example.app", PairingConfigDuration.Default);

            Assert.NotEqual(first.ConfigId, second.ConfigId);
            Assert.NotEqual(
                cache.Find(first.ConfigId)?.Config.Enrollment?.Secret,
                cache.Find(second.ConfigId)?.Config.Enrollment?.Secret);
            Assert.All(
                cache.GetSnapshot(),
                item => Assert.Equal(1, item.Config.Enrollment?.MaxUses));
        }
        finally
        {
            foreach (var path in new[] { first?.DesktopPath, second?.DesktopPath })
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void Issue_AnyAppInviteUsesGenericExportName()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var identityStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var cache = new PairingConfigCache(
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var service = new PairingConfigService(environment.ApplicationPaths, identityStore, cache);
        PairingIssueResult? result = null;

        try
        {
            Assert.True(
                PairingConfigDuration.TryParse("6mo", out var duration, out var durationError),
                durationError);
            result = service.Issue(
                PairingConfig.AnyAppName,
                PairingConfig.AnyAppId,
                duration);

            var invite = Assert.IsType<PairingConfig>(cache.Find(result.ConfigId)?.Config);
            Assert.True(PairingConfig.TargetsAnyApp(invite.AppId));
            Assert.Equal(PairingConfig.AnyAppName, invite.AppName);
            Assert.Equal("ansight-any-app.ans.json", Path.GetFileName(result.DesktopPath));
            Assert.InRange(
                invite.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(9),
                DateTimeOffset.UtcNow.AddMinutes(11));
            Assert.InRange(
                invite.Enrollment!.GrantExpiresAt,
                DateTimeOffset.UtcNow.AddDays(89),
                DateTimeOffset.UtcNow.AddDays(91));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(result?.DesktopPath) && File.Exists(result.DesktopPath))
            {
                File.Delete(result.DesktopPath);
            }
        }
    }

    [Fact]
    public void IssueTransient_SeedsOneUseInviteWithoutPublishingAFileResult()
    {
        using var environment = new TestSupport.TestEnvironment();
        using var identityStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var cache = new PairingConfigCache(
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var service = new PairingConfigService(environment.ApplicationPaths, identityStore, cache);
        Assert.True(
            PairingConfigDuration.TryParse("10m", out var duration, out var durationError),
            durationError);

        var result = service.IssueTransient("Workspace test", "com.example.testapp", duration);

        var invite = Assert.IsType<CachedPairingConfig>(cache.Find(result.ConfigId));
        var enrollment = Assert.IsType<PairingEnrollment>(invite.Config.Enrollment);
        Assert.Equal("com.example.testapp", invite.Config.AppId);
        Assert.False(invite.Consumed);
        Assert.Equal(1, enrollment.MaxUses);
        Assert.False(string.IsNullOrWhiteSpace(enrollment.Secret));
        Assert.Equal("write", enrollment.MaxToolPolicy);
        Assert.Equal(result.ExpiresAt, invite.Config.ExpiresAt);
        Assert.Equal("workspace-test", invite.SourceKind);
    }
}
