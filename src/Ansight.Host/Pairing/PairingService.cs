using System.ComponentModel.Composition;

namespace Ansight.Host.Pairing;

[Export(typeof(PairingService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class PairingService : Workspaces.Enrollment.IWorkspaceTestEnrollmentIssuer
{
    private const string DefaultIconGlyph = "\ue7ba";
    private readonly IPairingConfigService pairingConfigService;
    private readonly IPairingConfigCache pairingConfigCache;
    private readonly IKnownAppStore knownAppStore;

    [ImportingConstructor]
    internal PairingService(
        IPairingConfigService pairingConfigService,
        IPairingConfigCache pairingConfigCache,
        IKnownAppStore knownAppStore)
    {
        this.pairingConfigService = pairingConfigService
                                    ?? throw new ArgumentNullException(nameof(pairingConfigService));
        this.pairingConfigCache = pairingConfigCache
                                  ?? throw new ArgumentNullException(nameof(pairingConfigCache));
        this.knownAppStore = knownAppStore ?? throw new ArgumentNullException(nameof(knownAppStore));
    }

    public PairingInviteIssueResult Issue(
        string? appId = null,
        string? appName = null,
        string? duration = null)
    {
        if (!PairingConfigDuration.TryParse(duration, out var parsedDuration, out var durationError))
        {
            return new PairingInviteIssueResult(
                false,
                durationError ?? "Invalid enrollment duration.",
                null,
                null,
                null,
                null);
        }

        var anyApp = string.IsNullOrWhiteSpace(appId);
        var normalizedAppId = anyApp ? PairingConfig.AnyAppId : appId!.Trim();
        var normalizedAppName = anyApp
            ? PairingConfig.AnyAppName
            : string.IsNullOrWhiteSpace(appName)
                ? knownAppStore.TryGet(normalizedAppId, out var knownApp) && knownApp is not null
                    ? knownApp.Name
                    : normalizedAppId
                : appName.Trim();
        var result = pairingConfigService.Issue(normalizedAppName, normalizedAppId, parsedDuration);
        var cached = pairingConfigCache.Find(result.ConfigId);
        if (cached is null)
        {
            return new PairingInviteIssueResult(
                false,
                $"Enrollment invite '{result.ConfigId}' was issued but could not be reloaded.",
                null,
                result.DesktopPath,
                null,
                parsedDuration.Display);
        }

        if (!anyApp)
        {
            knownAppStore.EnsureKnown(
                normalizedAppId,
                normalizedAppName,
                DefaultIconGlyph,
                seenAtUtc: cached.Config.IssuedAt);
        }

        return new PairingInviteIssueResult(
            true,
            anyApp
                ? $"Issued generic enrollment invite '{result.ConfigId}'."
                : $"Issued enrollment invite '{result.ConfigId}' for '{normalizedAppId}'.",
            ToSummary(cached, DateTimeOffset.UtcNow),
            result.DesktopPath,
            PublicPairingConfigJson.Serialize(cached.Config, indented: true),
            parsedDuration.Display);
    }

    public IReadOnlyList<PairingInviteSummary> List(
        string? appId = null,
        bool includeConsumed = true,
        bool includeExpired = true)
    {
        var normalizedAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var now = DateTimeOffset.UtcNow;
        return pairingConfigCache.GetSnapshot()
            .Where(item => PairingProtocolPolicy.IsEnabled(item.Config))
            .Where(item => normalizedAppId is null
                           || PairingConfig.TargetsAnyApp(item.Config.AppId)
                           || string.Equals(item.Config.AppId, normalizedAppId, StringComparison.Ordinal))
            .Where(item => includeConsumed || !item.Consumed)
            .Where(item => includeExpired || item.Config.ExpiresAt >= now)
            .OrderByDescending(static item => item.Config.IssuedAt)
            .Select(item => ToSummary(item, now))
            .ToArray();
    }

    public IReadOnlyList<CachedPairingConfig> ListLocalConfigs()
    {
        return pairingConfigCache.GetSnapshot();
    }

    public CachedPairingConfig? FindLocalConfig(string inviteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        return pairingConfigCache.Find(inviteId.Trim());
    }

    public void ImportLocalConfig(CachedPairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        pairingConfigCache.Add(config);
    }

    public bool RemoveLocalConfig(string inviteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        return pairingConfigCache.Remove(inviteId.Trim());
    }

    public PairingQrResult CreateQr(
        string inviteId,
        string outputPath,
        string? hostAddress = null,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var codeResult = CreateCode(inviteId, hostAddress);
        if (!codeResult.IsSuccess || string.IsNullOrWhiteSpace(codeResult.PairingCode))
        {
            return QrFailure(codeResult.InviteId, codeResult.Message);
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        try
        {
            PairingQrPngWriter.Write(codeResult.PairingCode, fullOutputPath, overwrite);
            return new PairingQrResult(
                true,
                $"Wrote enrollment QR for '{codeResult.InviteId}' to '{fullOutputPath}'.",
                codeResult.InviteId,
                fullOutputPath,
                codeResult.PairingCode,
                codeResult.HostAddresses);
        }
        catch (IOException exception)
        {
            return QrFailure(codeResult.InviteId, $"Could not write enrollment QR: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return QrFailure(codeResult.InviteId, $"Could not write enrollment QR: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return QrFailure(codeResult.InviteId, $"Could not create enrollment QR: {exception.Message}");
        }
    }

    public PairingCodeResult CreateCode(
        string inviteId,
        string? hostAddress = null)
        => CreateCode(inviteId, hostAddress, "cli-code");

    internal UnattendedEnrollmentIssueResult IssueUnattendedEnrollment(
        string appId,
        string? appName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        var normalizedAppName = string.IsNullOrWhiteSpace(appName)
            ? knownAppStore.TryGet(normalizedAppId, out var knownApp) && knownApp is not null
                ? knownApp.Name
                : normalizedAppId
            : appName.Trim();
        var issue = pairingConfigService.IssueTransient(
            normalizedAppName,
            normalizedAppId,
            PairingConfigDuration.Default);
        knownAppStore.EnsureKnown(
            normalizedAppId,
            normalizedAppName,
            DefaultIconGlyph,
            seenAtUtc: DateTimeOffset.UtcNow);
        var code = CreateCode(issue.ConfigId, hostAddress: null, source: "workspace-test");
        if (!code.IsSuccess || string.IsNullOrWhiteSpace(code.PairingCode))
        {
            pairingConfigCache.Remove(issue.ConfigId);
            return UnattendedEnrollmentIssueResult.Failure(code.Message);
        }

        return UnattendedEnrollmentIssueResult.Success(
            issue.ConfigId,
            code.PairingCode,
            issue.ExpiresAt);
    }

    internal void RevokeUnconsumedEnrollment(string? inviteId, string appId)
    {
        if (string.IsNullOrWhiteSpace(inviteId) || string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        var item = pairingConfigCache.Find(inviteId.Trim());
        if (item is not null
            && !item.Consumed
            && string.Equals(item.Config.AppId, appId.Trim(), StringComparison.Ordinal))
        {
            pairingConfigCache.Remove(item.Config.ConfigId);
        }
    }

    UnattendedEnrollmentIssueResult Workspaces.Enrollment.IWorkspaceTestEnrollmentIssuer.Issue(string appId)
        => IssueUnattendedEnrollment(appId);

    void Workspaces.Enrollment.IWorkspaceTestEnrollmentIssuer.RevokeUnconsumed(string? inviteId, string appId)
        => RevokeUnconsumedEnrollment(inviteId, appId);

    private PairingCodeResult CreateCode(
        string inviteId,
        string? hostAddress,
        string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var normalizedInviteId = inviteId.Trim();
        var item = pairingConfigCache.Find(normalizedInviteId);
        if (item is null || !PairingProtocolPolicy.IsEnabled(item.Config))
        {
            return CodeFailure(normalizedInviteId, $"Enrollment invite '{normalizedInviteId}' was not found.");
        }

        if (item.Consumed)
        {
            return CodeFailure(normalizedInviteId, $"Enrollment invite '{normalizedInviteId}' has already been consumed.");
        }

        if (item.Config.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return CodeFailure(normalizedInviteId, $"Enrollment invite '{normalizedInviteId}' has expired.");
        }

        var discovery = AddressDiscovery.Capture();
        var addresses = AddressDiscovery.ApplyAdditionalHostAddresses(
            discovery.Addresses,
            hostAddress);
        if (!string.IsNullOrWhiteSpace(hostAddress)
            && !addresses.Contains(hostAddress.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return CodeFailure(
                normalizedInviteId,
                $"Host address '{hostAddress.Trim()}' is not a usable non-loopback IP address.");
        }

        if (addresses.Length == 0)
        {
            return CodeFailure(
                normalizedInviteId,
                "No local network address is available. Connect this machine to the phone's network or pass --host-address <LAN-IP>.");
        }

        try
        {
            var document = PairingConfigDocumentFactory.Create(
                item.Config,
                addresses,
                item.Config.Host.HostName,
                AddressDiscovery.ResolveWifiNetworkName(discovery),
                source);
            var pairingCode = PairingConfigDocumentFactory.SerializeCompactCode(document);
            return new PairingCodeResult(
                true,
                $"Created one-time pairing code for '{normalizedInviteId}'.",
                normalizedInviteId,
                pairingCode,
                addresses);
        }
        catch (InvalidOperationException exception)
        {
            return CodeFailure(normalizedInviteId, $"Could not create pairing code: {exception.Message}");
        }
    }

    public PairingInviteDetail? Get(string inviteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        var item = pairingConfigCache.Find(inviteId.Trim());
        return item is null || !PairingProtocolPolicy.IsEnabled(item.Config)
            ? null
            : new PairingInviteDetail(
                ToSummary(item, DateTimeOffset.UtcNow),
                PublicPairingConfigJson.Serialize(item.Config, indented: true));
    }

    public PairingOperationResult Revoke(
        string inviteId,
        string? expectedAppId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);
        var normalizedInviteId = inviteId.Trim();
        var item = pairingConfigCache.Find(normalizedInviteId);
        var summary = item is null ? null : ToSummary(item, DateTimeOffset.UtcNow);
        if (item is not null
            && !string.IsNullOrWhiteSpace(expectedAppId)
            && !string.Equals(item.Config.AppId, expectedAppId.Trim(), StringComparison.Ordinal))
        {
            return new PairingOperationResult(
                false,
                $"Enrollment invite '{normalizedInviteId}' belongs to '{item.Config.AppId}', not '{expectedAppId.Trim()}'.",
                summary);
        }

        if (item is not null && string.Equals(item.SourceKind, "team-sync", StringComparison.Ordinal))
        {
            return new PairingOperationResult(
                false,
                "Team-synced enrollment invites are read-only. Revoke the invite from the team portal.",
                summary);
        }

        var removed = pairingConfigCache.Remove(normalizedInviteId);
        return removed
            ? new PairingOperationResult(true, $"Revoked enrollment invite '{normalizedInviteId}'.", summary)
            : new PairingOperationResult(false, $"Enrollment invite '{normalizedInviteId}' was not found.");
    }

    private static PairingInviteSummary ToSummary(CachedPairingConfig item, DateTimeOffset now)
    {
        var expired = item.Config.ExpiresAt < now;
        return new PairingInviteSummary(
            item.Config.ConfigId,
            PairingConfig.TargetsAnyApp(item.Config.AppId) ? "any-app" : "app",
            item.Config.AppId,
            item.Config.AppName,
            item.Config.Schema,
            item.Config.IssuedAt,
            item.Config.ExpiresAt,
            item.Consumed,
            expired,
            expired ? "Expired" : item.Consumed ? "Consumed" : "Available",
            item.SourceKind,
            string.Equals(item.SourceKind, "team-sync", StringComparison.Ordinal));
    }

    private static PairingQrResult QrFailure(string inviteId, string message)
        => new(false, message, inviteId, null, null, Array.Empty<string>());

    private static PairingCodeResult CodeFailure(string inviteId, string message)
        => new(false, message, inviteId, null, Array.Empty<string>());
}

internal sealed record UnattendedEnrollmentIssueResult(
    bool IsSuccess,
    string Message,
    string? InviteId,
    string? Payload,
    DateTimeOffset? ExpiresAtUtc)
{
    public static UnattendedEnrollmentIssueResult Success(
        string inviteId,
        string payload,
        DateTimeOffset expiresAtUtc)
        => new(
            true,
            "Issued a one-use enrollment payload for the physical test target.",
            inviteId,
            payload,
            expiresAtUtc);

    public static UnattendedEnrollmentIssueResult Failure(string message)
        => new(false, message, null, null, null);
}
