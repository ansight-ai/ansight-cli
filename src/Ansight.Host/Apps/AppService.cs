using System.ComponentModel.Composition;

namespace Ansight.Host.Apps;

[Export(typeof(AppService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class AppService
{
    private const string DefaultIconGlyph = "\ue7ba";
    private readonly IKnownAppStore knownAppStore;
    private readonly IRuntimeState runtimeState;
    private readonly IAppToolBridge appToolBridge;
    private readonly IPairingConfigCache pairingConfigCache;

    [ImportingConstructor]
    internal AppService(
        IKnownAppStore knownAppStore,
        IRuntimeState runtimeState,
        IAppToolBridge appToolBridge,
        IPairingConfigCache pairingConfigCache)
    {
        this.knownAppStore = knownAppStore ?? throw new ArgumentNullException(nameof(knownAppStore));
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
        this.pairingConfigCache = pairingConfigCache
                                  ?? throw new ArgumentNullException(nameof(pairingConfigCache));
        this.knownAppStore.Changed += KnownAppStoreOnChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<KnownAppDefinition> GetDefinitions()
    {
        return knownAppStore.GetSnapshot();
    }

    public bool TryGetDefinition(string appId, out KnownAppDefinition? app)
    {
        return knownAppStore.TryGet(appId, out app);
    }

    public void EnsureKnown(
        string appId,
        string? name,
        string iconGlyph,
        string? codebasePath = null,
        DateTimeOffset? seenAtUtc = null,
        string? sourceKind = null,
        string? sourceTeamId = null,
        string? sourceTeamName = null)
    {
        knownAppStore.EnsureKnown(
            appId,
            name,
            iconGlyph,
            codebasePath,
            seenAtUtc,
            sourceKind,
            sourceTeamId,
            sourceTeamName);
    }

    public void SaveDefinition(KnownAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);
        knownAppStore.Upsert(app);
    }

    public bool RemoveDefinition(string appId)
    {
        return knownAppStore.Remove(appId);
    }

    public IReadOnlyList<AppDescriptor> List()
    {
        var knownApps = knownAppStore.GetSnapshot();
        var sessions = runtimeState.GetSessionSummaries();
        var knownById = knownApps.ToDictionary(static app => app.AppId, StringComparer.Ordinal);
        var inviteCountsByAppId = pairingConfigCache.GetSnapshot()
            .Where(static item => PairingProtocolPolicy.IsEnabled(item.Config))
            .GroupBy(static item => item.Config.AppId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var appIds = knownApps.Select(static app => app.AppId)
            .Concat(sessions.Select(static session => session.AppId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return appIds.Select(appId => BuildDescriptor(
                appId,
                knownById.GetValueOrDefault(appId),
                sessions.Where(session => string.Equals(session.AppId, appId, StringComparison.Ordinal)).ToArray(),
                inviteCountsByAppId.GetValueOrDefault(appId)))
            .OrderBy(static app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static app => app.AppId, StringComparer.Ordinal)
            .ToArray();
    }

    public AppDescriptor? Get(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        return List().FirstOrDefault(app =>
            string.Equals(app.AppId, normalizedAppId, StringComparison.Ordinal));
    }

    public AppOperationResult Register(AppRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            return new AppOperationResult(false, "App ID is required.");
        }

        var appId = request.AppId.Trim();
        knownAppStore.TryGet(appId, out var existing);

        var linksRepositoryWorkspace = !string.IsNullOrWhiteSpace(request.CodebasePath);
        var codebasePath = existing?.CodebasePath;
        if (linksRepositoryWorkspace)
        {
            codebasePath = Path.GetFullPath(request.CodebasePath!.Trim());
            if (!Directory.Exists(codebasePath))
            {
                return new AppOperationResult(false, $"Codebase folder '{codebasePath}' was not found.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        knownAppStore.Upsert(new KnownAppDefinition
        {
            AppId = appId,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? string.IsNullOrWhiteSpace(existing?.Name) ? appId : existing.Name
                : request.Name.Trim(),
            IconGlyph = string.IsNullOrWhiteSpace(existing?.IconGlyph) ? DefaultIconGlyph : existing.IconGlyph,
            IconImagePath = existing?.IconImagePath,
            CodebasePath = codebasePath,
            RepositoryAutomationsEnabled = linksRepositoryWorkspace
                                           || existing?.RepositoryAutomationsEnabled == true,
            SourceKind = existing?.SourceKind,
            SourceTeamId = existing?.SourceTeamId,
            SourceTeamName = existing?.SourceTeamName,
            FirstSeenUtc = existing?.FirstSeenUtc ?? now,
            LastSeenUtc = now
        });
        var app = Get(appId);
        return new AppOperationResult(
            true,
            existing is null ? $"Registered app '{appId}'." : $"Updated registered app '{appId}'.",
            app);
    }

    public AppOperationResult UpdateRegistration(string previousAppId, AppRegistrationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousAppId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            return new AppOperationResult(false, "App ID is required.");
        }

        var normalizedPreviousAppId = previousAppId.Trim();
        var normalizedAppId = request.AppId.Trim();
        if (string.Equals(normalizedPreviousAppId, normalizedAppId, StringComparison.Ordinal))
        {
            return Register(request);
        }

        if (!knownAppStore.TryGet(normalizedPreviousAppId, out var existing) || existing is null)
        {
            return new AppOperationResult(false, $"App '{normalizedPreviousAppId}' is not registered.");
        }

        if (knownAppStore.TryGet(normalizedAppId, out _))
        {
            return new AppOperationResult(false, $"App '{normalizedAppId}' is already registered.", Get(normalizedAppId));
        }

        var codebasePath = existing.CodebasePath;
        if (!string.IsNullOrWhiteSpace(request.CodebasePath))
        {
            codebasePath = Path.GetFullPath(request.CodebasePath.Trim());
            if (!Directory.Exists(codebasePath))
            {
                return new AppOperationResult(false, $"Codebase folder '{codebasePath}' was not found.", Get(normalizedPreviousAppId));
            }
        }

        var now = DateTimeOffset.UtcNow;
        knownAppStore.Upsert(new KnownAppDefinition
        {
            AppId = normalizedAppId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
            IconGlyph = existing.IconGlyph,
            IconImagePath = existing.IconImagePath,
            CodebasePath = codebasePath,
            RepositoryAutomationsEnabled = existing.RepositoryAutomationsEnabled,
            SourceKind = existing.SourceKind,
            SourceTeamId = existing.SourceTeamId,
            SourceTeamName = existing.SourceTeamName,
            SourceSyncedAtUtc = existing.SourceSyncedAtUtc,
            FirstSeenUtc = existing.FirstSeenUtc,
            LastSeenUtc = now
        });
        if (!knownAppStore.Remove(normalizedPreviousAppId))
        {
            knownAppStore.Remove(normalizedAppId);
            return new AppOperationResult(false, $"App '{normalizedPreviousAppId}' could not be updated.");
        }

        return new AppOperationResult(
            true,
            $"Updated app package identifier from '{normalizedPreviousAppId}' to '{normalizedAppId}'.",
            Get(normalizedAppId));
    }

    public AppOperationResult Remove(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        var existing = Get(normalizedAppId);
        var removed = knownAppStore.Remove(normalizedAppId);
        return removed
            ? new AppOperationResult(true, $"Removed app '{normalizedAppId}'.", existing)
            : new AppOperationResult(false, $"App '{normalizedAppId}' is not registered.", existing);
    }

    public AppOperationResult ConfigureRepositoryWorkspace(string appId, string codebasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(codebasePath);
        var normalizedAppId = appId.Trim();
        var normalizedCodebasePath = Path.GetFullPath(codebasePath.Trim());
        if (!Directory.Exists(normalizedCodebasePath))
        {
            return new AppOperationResult(
                false,
                $"Codebase folder '{normalizedCodebasePath}' was not found.",
                Get(normalizedAppId));
        }

        var now = DateTimeOffset.UtcNow;
        knownAppStore.TryGet(normalizedAppId, out var existing);
        knownAppStore.Upsert(new KnownAppDefinition
        {
            AppId = normalizedAppId,
            Name = string.IsNullOrWhiteSpace(existing?.Name) ? normalizedAppId : existing.Name,
            IconGlyph = string.IsNullOrWhiteSpace(existing?.IconGlyph) ? DefaultIconGlyph : existing.IconGlyph,
            IconImagePath = existing?.IconImagePath,
            CodebasePath = normalizedCodebasePath,
            RepositoryAutomationsEnabled = true,
            SourceKind = existing?.SourceKind,
            SourceTeamId = existing?.SourceTeamId,
            SourceTeamName = existing?.SourceTeamName,
            FirstSeenUtc = existing?.FirstSeenUtc ?? now,
            LastSeenUtc = now
        });
        return new AppOperationResult(
            true,
            $"Connected app '{normalizedAppId}' to trusted workspace '{normalizedCodebasePath}'.",
            Get(normalizedAppId));
    }

    public AppOperationResult ClearRepositoryWorkspace(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        if (!knownAppStore.TryGet(normalizedAppId, out var existing) || existing is null)
        {
            return new AppOperationResult(false, $"App '{normalizedAppId}' is not registered.");
        }

        knownAppStore.Upsert(CopyDefinition(
            existing,
            codebasePath: null,
            repositoryAutomationsEnabled: false));
        return new AppOperationResult(
            true,
            $"Disconnected app '{normalizedAppId}' from its repository workspace.",
            Get(normalizedAppId));
    }

    public AppOperationResult SetRepositoryAutomationsEnabled(string appId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalizedAppId = appId.Trim();
        if (!knownAppStore.TryGet(normalizedAppId, out var existing) || existing is null)
        {
            return new AppOperationResult(false, $"App '{normalizedAppId}' is not registered.");
        }

        if (enabled && string.IsNullOrWhiteSpace(existing.CodebasePath))
        {
            return new AppOperationResult(
                false,
                $"Link app '{normalizedAppId}' to a repository workspace before enabling automations.",
                Get(normalizedAppId));
        }

        knownAppStore.Upsert(CopyDefinition(
            existing,
            existing.CodebasePath,
            enabled));
        return new AppOperationResult(
            true,
            enabled
                ? $"Enabled repository automations for '{normalizedAppId}'."
                : $"Disabled repository automations for '{normalizedAppId}'.",
            Get(normalizedAppId));
    }

    private static KnownAppDefinition CopyDefinition(
        KnownAppDefinition source,
        string? codebasePath,
        bool repositoryAutomationsEnabled)
        => new()
        {
            AppId = source.AppId,
            Name = source.Name,
            IconGlyph = source.IconGlyph,
            IconImagePath = source.IconImagePath,
            CodebasePath = codebasePath,
            RepositoryAutomationsEnabled = repositoryAutomationsEnabled,
            SourceKind = source.SourceKind,
            SourceTeamId = source.SourceTeamId,
            SourceTeamName = source.SourceTeamName,
            SourceSyncedAtUtc = source.SourceSyncedAtUtc,
            FirstSeenUtc = source.FirstSeenUtc,
            LastSeenUtc = DateTimeOffset.UtcNow
        };

    private AppDescriptor BuildDescriptor(
        string appId,
        KnownAppDefinition? knownApp,
        IReadOnlyList<AppSessionSnapshot> sessions,
        int enrollmentInviteCount)
    {
        var firstSeenUtc = knownApp?.FirstSeenUtc
                           ?? sessions.Select(static session => session.CreatedUtc).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
        var lastSeenUtc = new[]
            {
                knownApp?.LastSeenUtc,
                sessions.Count == 0 ? null : sessions.Max(static session => session.LastUpdatedUtc)
            }
            .Where(static value => value is not null)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(firstSeenUtc)
            .Max();
        var registeredName = string.IsNullOrWhiteSpace(knownApp?.Name)
                             || string.Equals(knownApp.Name, appId, StringComparison.OrdinalIgnoreCase)
            ? null
            : knownApp.Name;
        return new AppDescriptor(
            appId,
            registeredName
            ?? sessions.Select(static session => session.DeviceProfile?.App?.AppName)
                .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name))
            ?? sessions.Select(static session => session.ClientName)
                .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name))
            ?? appId,
            knownApp?.CodebasePath,
            !string.IsNullOrWhiteSpace(knownApp?.CodebasePath)
            && Directory.Exists(knownApp.CodebasePath),
            knownApp?.IconImagePath,
            knownApp?.RepositoryAutomationsEnabled == true,
            knownApp?.SourceKind,
            knownApp?.SourceTeamId,
            knownApp?.SourceTeamName,
            firstSeenUtc,
            lastSeenUtc,
            sessions.Count,
            sessions.Count(session => appToolBridge.IsSessionConnected(session.SessionId) || runtimeState.IsDeviceSessionActive(session.SessionId)),
            sessions.Sum(static session => session.Analyses.Count),
            enrollmentInviteCount);
    }

    private void KnownAppStoreOnChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
