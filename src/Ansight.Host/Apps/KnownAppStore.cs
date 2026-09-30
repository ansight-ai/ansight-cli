namespace Ansight.Host.Apps;

[Export(typeof(IKnownAppStore))]
internal sealed class KnownAppStore : IKnownAppStore
{
    private readonly Lock gate = new();
    private readonly string storePath;
    private KnownAppDocument document;

    [ImportingConstructor]
    public KnownAppStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        storePath = Path.Combine(applicationPaths.ApplicationDataPath, "known-apps.json");
        document = LoadDocument(storePath);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<KnownAppDefinition> GetSnapshot()
    {
        lock (gate)
        {
            ReloadDocument();
            return document.Apps
                .Select(Clone)
                .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string appId, out KnownAppDefinition? app)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            app = null;
            return false;
        }

        lock (gate)
        {
            ReloadDocument();
            var existing = document.Apps.FirstOrDefault(item =>
                string.Equals(item.AppId, appId.Trim(), StringComparison.Ordinal));
            if (existing is null)
            {
                app = null;
                return false;
            }

            app = Clone(existing);
            return true;
        }
    }

    public void Upsert(KnownAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var sanitized = Sanitize(app);

        lock (gate)
        {
            ReloadDocument();
            var index = document.Apps.FindIndex(item =>
                string.Equals(item.AppId, sanitized.AppId, StringComparison.Ordinal));
            if (index >= 0)
            {
                var existing = document.Apps[index];
                sanitized.FirstSeenUtc = existing.FirstSeenUtc;
                if (string.IsNullOrWhiteSpace(sanitized.SourceKind))
                {
                    sanitized.SourceKind = existing.SourceKind;
                    sanitized.SourceTeamId = existing.SourceTeamId;
                    sanitized.SourceTeamName = existing.SourceTeamName;
                    sanitized.SourceSyncedAtUtc = existing.SourceSyncedAtUtc;
                }

                document.Apps[index] = sanitized;
            }
            else
            {
                document.Apps.Add(sanitized);
            }

            Persist();
        }

        NotifyChanged();
    }

    public bool Remove(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        lock (gate)
        {
            ReloadDocument();
            var removed = document.Apps.RemoveAll(item =>
                string.Equals(item.AppId, appId.Trim(), StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            Persist();
        }

        NotifyChanged();
        return true;
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
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        var normalizedAppId = appId.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? normalizedAppId : name.Trim();
        var normalizedGlyph = string.IsNullOrWhiteSpace(iconGlyph) ? "\ue7ba" : iconGlyph;
        var normalizedCodebase = string.IsNullOrWhiteSpace(codebasePath) ? null : codebasePath.Trim();
        var normalizedSourceKind = string.IsNullOrWhiteSpace(sourceKind) ? null : sourceKind.Trim();
        var normalizedSourceTeamId = string.IsNullOrWhiteSpace(sourceTeamId) ? null : sourceTeamId.Trim();
        var normalizedSourceTeamName = string.IsNullOrWhiteSpace(sourceTeamName) ? null : sourceTeamName.Trim();
        var timestamp = seenAtUtc ?? DateTimeOffset.UtcNow;
        var shouldNotifyChanged = false;

        lock (gate)
        {
            ReloadDocument();
            var existing = document.Apps.FirstOrDefault(item =>
                string.Equals(item.AppId, normalizedAppId, StringComparison.Ordinal));
            if (existing is null)
            {
                document.Apps.Add(new KnownAppDefinition
                {
                    AppId = normalizedAppId,
                    Name = normalizedName,
                    IconGlyph = normalizedGlyph,
                    IconImagePath = null,
                    CodebasePath = normalizedCodebase,
                    RepositoryAutomationsEnabled = normalizedCodebase is not null,
                    SourceKind = normalizedSourceKind,
                    SourceTeamId = normalizedSourceTeamId,
                    SourceTeamName = normalizedSourceTeamName,
                    SourceSyncedAtUtc = normalizedSourceKind is null ? null : DateTimeOffset.UtcNow,
                    FirstSeenUtc = timestamp,
                    LastSeenUtc = timestamp
                });
                Persist();
                shouldNotifyChanged = true;
            }
            else
            {
                var changed = false;
                if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(existing.Name)
                        || string.Equals(existing.Name, existing.AppId, StringComparison.Ordinal)))
                {
                    existing.Name = normalizedName;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(existing.IconGlyph) && !string.IsNullOrWhiteSpace(normalizedGlyph))
                {
                    existing.IconGlyph = normalizedGlyph;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(normalizedCodebase)
                    && !string.Equals(existing.CodebasePath, normalizedCodebase, StringComparison.Ordinal))
                {
                    existing.CodebasePath = normalizedCodebase;
                    existing.RepositoryAutomationsEnabled = true;
                    changed = true;
                }

                if (normalizedSourceKind is not null)
                {
                    if (!string.Equals(existing.SourceKind, normalizedSourceKind, StringComparison.Ordinal)
                        || !string.Equals(existing.SourceTeamId, normalizedSourceTeamId, StringComparison.Ordinal)
                        || !string.Equals(existing.SourceTeamName, normalizedSourceTeamName, StringComparison.Ordinal))
                    {
                        existing.SourceKind = normalizedSourceKind;
                        existing.SourceTeamId = normalizedSourceTeamId;
                        existing.SourceTeamName = normalizedSourceTeamName;
                        existing.SourceSyncedAtUtc = DateTimeOffset.UtcNow;
                        changed = true;
                    }
                }

                if (timestamp > existing.LastSeenUtc)
                {
                    existing.LastSeenUtc = timestamp;
                    changed = true;
                }

                if (changed)
                {
                    Persist();
                    shouldNotifyChanged = true;
                }
            }
        }

        if (shouldNotifyChanged)
        {
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadDocument()
    {
        document = LoadDocument(storePath);
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(document, JsonUtil.Pretty);
        var tempPath = storePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, storePath, overwrite: true);
    }

    private static KnownAppDocument LoadDocument(string storePath)
    {
        if (!File.Exists(storePath))
        {
            return new KnownAppDocument();
        }

        try
        {
            var json = File.ReadAllText(storePath);
            var document = JsonSerializer.Deserialize<KnownAppDocument>(json, JsonUtil.Compact);
            return document ?? new KnownAppDocument();
        }
        catch
        {
            return new KnownAppDocument();
        }
    }

    private static KnownAppDefinition Sanitize(KnownAppDefinition app)
    {
        var appId = string.IsNullOrWhiteSpace(app.AppId) ? throw new ArgumentException("App ID is required.", nameof(app)) : app.AppId.Trim();
        var name = string.IsNullOrWhiteSpace(app.Name) ? appId : app.Name.Trim();
        var iconGlyph = string.IsNullOrWhiteSpace(app.IconGlyph) ? "\ue7ba" : app.IconGlyph;

        return new KnownAppDefinition
        {
            AppId = appId,
            Name = name,
            IconGlyph = iconGlyph,
            IconImagePath = string.IsNullOrWhiteSpace(app.IconImagePath) ? null : app.IconImagePath.Trim(),
            CodebasePath = string.IsNullOrWhiteSpace(app.CodebasePath) ? null : app.CodebasePath.Trim(),
            RepositoryAutomationsEnabled = app.RepositoryAutomationsEnabled,
            SourceKind = string.IsNullOrWhiteSpace(app.SourceKind) ? null : app.SourceKind.Trim(),
            SourceTeamId = string.IsNullOrWhiteSpace(app.SourceTeamId) ? null : app.SourceTeamId.Trim(),
            SourceTeamName = string.IsNullOrWhiteSpace(app.SourceTeamName) ? null : app.SourceTeamName.Trim(),
            SourceSyncedAtUtc = app.SourceSyncedAtUtc,
            FirstSeenUtc = app.FirstSeenUtc == default ? DateTimeOffset.UtcNow : app.FirstSeenUtc,
            LastSeenUtc = app.LastSeenUtc == default ? DateTimeOffset.UtcNow : app.LastSeenUtc
        };
    }

    private static KnownAppDefinition Clone(KnownAppDefinition app)
    {
        return new KnownAppDefinition
        {
            AppId = app.AppId,
            Name = app.Name,
            IconGlyph = app.IconGlyph,
            IconImagePath = app.IconImagePath,
            CodebasePath = app.CodebasePath,
            RepositoryAutomationsEnabled = app.RepositoryAutomationsEnabled,
            SourceKind = app.SourceKind,
            SourceTeamId = app.SourceTeamId,
            SourceTeamName = app.SourceTeamName,
            SourceSyncedAtUtc = app.SourceSyncedAtUtc,
            FirstSeenUtc = app.FirstSeenUtc,
            LastSeenUtc = app.LastSeenUtc
        };
    }
}
