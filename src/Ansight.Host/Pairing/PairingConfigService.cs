namespace Ansight.Host.Pairing;

using Ansight.Pairing;

[Export(typeof(IPairingConfigService))]
internal sealed class PairingConfigService : IPairingConfigService
{
    private static readonly TimeSpan GenericInviteLifetime = TimeSpan.FromMinutes(10);
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private readonly IApplicationPaths applicationPaths;
    private readonly IIdentityStore hostIdentityStore;
    private readonly IPairingConfigCache pairingCache;

    [ImportingConstructor]
    public PairingConfigService(
        IApplicationPaths applicationPaths,
        IIdentityStore hostIdentityStore,
        IPairingConfigCache pairingCache)
    {
        this.applicationPaths = applicationPaths;
        this.hostIdentityStore = hostIdentityStore;
        this.pairingCache = pairingCache;
    }

    public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration)
    {
        var config = CreateConfig(appName, appId, duration, "read");
        pairingCache.Add(config);

        var result = WritePublicConfig(config);
        log.Info($"enrollment_invite_issued protocol=v2 transport=ws appId={appId} appName={appName} inviteId={config.ConfigId} duration={duration.Display} expiresAtUtc={config.ExpiresAt:O} desktopPath={result.DesktopPath}");
        return result;
    }

    public PairingTransientIssueResult IssueTransient(
        string appName,
        string appId,
        PairingConfigDuration duration)
    {
        var config = CreateConfig(appName, appId, duration, "write");
        pairingCache.Add(new CachedPairingConfig
        {
            Config = config,
            Consumed = false,
            SourceKind = "workspace-test"
        });
        log.Info($"enrollment_invite_issued protocol=v2 transport=ws source=workspace-test appId={appId} appName={appName} inviteId={config.ConfigId} duration={duration.Display} expiresAtUtc={config.ExpiresAt:O}");
        return new PairingTransientIssueResult(config.ConfigId, config.ExpiresAt);
    }

    private PairingConfig CreateConfig(
        string appName,
        string appId,
        PairingConfigDuration duration,
        string maximumToolPolicy)
    {
        var now = DateTimeOffset.UtcNow;
        var requestedExpiry = duration.Apply(now);
        var maximumInviteExpiry = PairingConfig.TargetsAnyApp(appId)
            ? now.Add(GenericInviteLifetime)
            : now.AddHours(24);
        var expiresAt = requestedExpiry < maximumInviteExpiry
            ? requestedExpiry
            : maximumInviteExpiry;
        var grantExpiresAt = requestedExpiry < now.AddDays(90) ? requestedExpiry : now.AddDays(90);
        var hostIdentity = hostIdentityStore.Current;

        return new PairingConfig
        {
            Schema = PairingConfig.SchemaName,
            ConfigId = Guid.NewGuid().ToString("N"),
            AppId = appId,
            AppName = appName,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            MinProtocolVersion = 2,
            AllowedTransports = [PairingTransportNames.Ws],
            Host = new PairingHost
            {
                HostId = hostIdentity.HostId,
                HostName = hostIdentity.HostName,
                DiscoveryPort = ProtocolDefaults.DiscoveryPort
            },
            Enrollment = new PairingEnrollment
            {
                Secret = CryptoUtil.CreateBase64UrlRandom(32),
                ExpiresAt = expiresAt,
                GrantExpiresAt = grantExpiresAt,
                MaxUses = 1,
                MaxToolPolicy = PairingToolPolicy.Normalize(maximumToolPolicy)
            }
        };
    }

    private PairingIssueResult WritePublicConfig(PairingConfig config)
    {
        var desktopFileName = PairingConfig.TargetsAnyApp(config.AppId)
            ? "ansight-any-app.ans.json"
            : $"{FileNameUtil.Sanitize(config.AppId)}.ans.json";
        var desktopPath = Path.Combine(GetDesktopDirectory(), desktopFileName);
        var json = PublicPairingConfigJson.Serialize(config, indented: true);

        try
        {
            File.WriteAllText(desktopPath, json);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            var appsPath = AppDefinitionStorage.GetDirectoryPath(applicationPaths.ApplicationDataPath);
            Directory.CreateDirectory(appsPath);
            desktopPath = Path.Combine(appsPath, desktopFileName);
            File.WriteAllText(desktopPath, json);
        }

        RestrictInviteFilePermissions(desktopPath);

        return new PairingIssueResult(config.ConfigId, desktopPath, config.ExpiresAt);
    }

    private static void RestrictInviteFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string GetDesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? AppContext.BaseDirectory : desktop;
    }
}
