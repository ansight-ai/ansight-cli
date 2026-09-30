using System.Text.Json;

namespace Ansight.Analytics;

public sealed class AnalyticsSettingsStore
{
    private const string AnalyticsDirectoryName = "analytics";
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string settingsPath;
    private readonly string dataDirectory;

    public AnalyticsSettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = dataDirectory;
        AnalyticsDirectoryPath = Path.Combine(dataDirectory, AnalyticsDirectoryName);
        settingsPath = Path.Combine(AnalyticsDirectoryPath, SettingsFileName);
    }

    public string AnalyticsDirectoryPath { get; }

    public string SettingsPath => settingsPath;

    public bool IsDetailedTrackingEnabled
    {
        get
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("ANSIGHT_ANALYTICS_DISABLED"), "true", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!File.Exists(settingsPath))
                {
                    return false;
                }

                var accountId = AnalyticsAccount.Read(dataDirectory);
                var saved = JsonSerializer.Deserialize<AnalyticsSettings>(File.ReadAllText(settingsPath), jsonOptions);
                return accountId is not null
                       && saved?.DetailedTrackingEnabled == true
                       && string.Equals(saved.AccountId, accountId, StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException)
            {
                return false;
            }
        }
    }

    // Consent alone does not authorize per-action analytics for an unsigned user.
    public bool IsDetailedTrackingActive => IsDetailedTrackingEnabled
                                            && AnalyticsAccount.Read(dataDirectory) == AnalyticsAccount.ReadPersisted(dataDirectory);

    public bool HasSignedInAccount => AnalyticsAccount.Read(dataDirectory) is not null;

    public void SetDetailedTrackingEnabled(bool enabled)
    {
        var accountId = AnalyticsAccount.Read(dataDirectory);
        if (enabled && accountId is null)
            throw new InvalidOperationException("Sign in before opting in to detailed analytics.");
        Directory.CreateDirectory(AnalyticsDirectoryPath);
        var temporaryPath = $"{settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new AnalyticsSettings(enabled, enabled ? accountId : null), jsonOptions));
            TryRestrictFilePermissions(temporaryPath);
            File.Move(temporaryPath, settingsPath, overwrite: true);
            TryRestrictFilePermissions(settingsPath);
            if (!enabled)
            {
                ProductUsage.Flush(dataDirectory, force: true);
                new EventOutbox(AnalyticsDirectoryPath).RemoveDetailedEvents();
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
