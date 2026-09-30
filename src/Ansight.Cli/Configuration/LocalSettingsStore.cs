using Ansight.Host;
using Ansight.Infrastructure.Preferences;

namespace Ansight.Cli.Configuration;

internal sealed class LocalSettingsStore
{
    public const string DefaultExplorerPath = "/ansight/";
    public const int DefaultExplorerPort = 47_231;

    private const string ExplorerPathKey = "localExplorerPath";
    private const string ExplorerPortKey = "localExplorerPort";
    private const string SettingsFileName = "cli-settings.json";
    private readonly IPreferencesStore preferences;

    public LocalSettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        SettingsPath = Path.Combine(dataDirectory, SettingsFileName);
        preferences = new FilePreferencesStore(SettingsPath);
    }

    internal LocalSettingsStore(IPreferencesStore preferences, string settingsPath)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public static CredentialSettings? ReadCredentials(string dataDirectory)
    {
        if (!File.Exists(Path.Combine(dataDirectory, SettingsFileName))) return null;
        var saved = new LocalSettingsStore(dataDirectory).Credentials;
        if (saved is not null && (saved.Provider is not ("protected-file" or "linux-secret-service")
            || (saved.Provider == "protected-file" && string.IsNullOrWhiteSpace(saved.KeyFile))
            || (saved.Provider == "linux-secret-service" && (saved.KeyFile is not null || saved.StoreFile is not null))))
            throw new CliUsageException("Saved credential settings are invalid. Restore the original provider and external key reference in cli-settings.json.");
        return saved;
    }

    public static string? ReadAndroidSdkRoot(string dataDirectory)
        => File.Exists(Path.Combine(dataDirectory, SettingsFileName))
            ? new LocalSettingsStore(dataDirectory).preferences.Get<string?>("androidSdkRoot", null)
            : null;

    public void SetAndroidSdkRoot(string sdkRoot) => preferences.Set("androidSdkRoot", Path.GetFullPath(sdkRoot));

    public CredentialSettings? Credentials => preferences.Get<CredentialSettings?>("credentials", null);

    public void SetCredentials(CredentialSettings settings) => preferences.Set("credentials", settings);

    public string? ExplorerPath
        => preferences.Contains(ExplorerPathKey)
            ? NormalizeStoredPath(preferences.Get(ExplorerPathKey, string.Empty))
            : DefaultExplorerPath;

    public int? ExplorerPort
        => preferences.Contains(ExplorerPortKey)
            ? NormalizeStoredPort(preferences.Get(ExplorerPortKey, 0))
            : DefaultExplorerPort;

    public bool HasExplorerPathPreference
        => preferences.Contains(ExplorerPathKey);

    public bool HasExplorerPortPreference
        => preferences.Contains(ExplorerPortKey);

    public string SetExplorerPath(string path)
    {
        var normalizedPath = NormalizeRequiredPath(path);
        preferences.Set(ExplorerPathKey, normalizedPath);
        return normalizedPath;
    }

    public bool ClearExplorerPath()
    {
        var wasPinned = ExplorerPath is not null;
        preferences.Set(ExplorerPathKey, string.Empty);
        return wasPinned;
    }

    public int SetExplorerPort(string port)
    {
        if (!int.TryParse(port, out var parsedPort)
            || parsedPort is < 1 or > 65_535)
        {
            throw new CliUsageException(
                "Explorer port must be an integer between 1 and 65535.");
        }

        preferences.Set(ExplorerPortKey, parsedPort);
        return parsedPort;
    }

    public bool ClearExplorerPort()
    {
        var wasPinned = ExplorerPort is not null;
        preferences.Set(ExplorerPortKey, 0);
        return wasPinned;
    }

    public static string? ResolveExplorerPath(
        CliArguments arguments,
        string optionName,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.HasFlag(optionName))
        {
            return arguments.RequireOption(optionName);
        }

        return new LocalSettingsStore(dataDirectory).ExplorerPath;
    }

    public static int ResolveExplorerPort(
        CliArguments arguments,
        string optionName,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.HasFlag(optionName))
        {
            return arguments.GetIntOption(optionName, 0, 0, 65_535);
        }

        return new LocalSettingsStore(dataDirectory).ExplorerPort ?? 0;
    }

    private static string? NormalizeStoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return ExplorerServer.NormalizeRequestedPath(path);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? NormalizeStoredPort(int port)
        => port is >= 1 and <= 65_535 ? port : null;

    private static string NormalizeRequiredPath(string path)
    {
        try
        {
            return ExplorerServer.NormalizeRequestedPath(path)
                   ?? throw new CliUsageException("Explorer path cannot be empty.");
        }
        catch (InvalidOperationException exception)
        {
            throw new CliUsageException(exception.Message);
        }
    }
}
