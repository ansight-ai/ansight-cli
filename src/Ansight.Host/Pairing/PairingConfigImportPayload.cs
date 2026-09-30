namespace Ansight.Host.Pairing;

public sealed record PairingConfigImportPayload(
    PairingConfigImportKind Kind,
    string AppId,
    string AppName,
    string? SourceConfigId,
    CachedPairingConfig? CachedConfig)
{
    public static PairingConfigImportPayload FromFullConfigExport(CachedPairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new PairingConfigImportPayload(
            PairingConfigImportKind.FullConfigExport,
            config.Config.AppId,
            config.Config.AppName,
            config.Config.ConfigId,
            config);
    }

    public static PairingConfigImportPayload FromPublicAppConfig(string appId, string appName, string? sourceConfigId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var normalizedAppId = appId.Trim();
        var normalizedAppName = string.IsNullOrWhiteSpace(appName)
            ? normalizedAppId
            : appName.Trim();
        var normalizedConfigId = string.IsNullOrWhiteSpace(sourceConfigId)
            ? null
            : sourceConfigId.Trim();

        return new PairingConfigImportPayload(
            PairingConfigImportKind.PublicAppConfig,
            normalizedAppId,
            normalizedAppName,
            normalizedConfigId,
            null);
    }
}
