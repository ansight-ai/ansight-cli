namespace Ansight.Host.Runtime.Configuration;

public sealed class RuntimeOptions
{
    public string? ExtensionsDirectory { get; init; }

    /// <summary>Optional embedding application's permission lifetime for background feature work.</summary>
    public CancellationToken FeatureLifetime { get; init; }

    public ISessionVideoEncoder? SessionVideoEncoder { get; init; }

    public string HostIdentityStorageKey { get; init; } = "host-identity";

    public string? HostIdentityName { get; init; }

    public string? BaseFolderPath { get; init; }

    public string? SecureStorageFilePath { get; init; }

    public string? SecureStorageKeyFilePath { get; init; }

    public string? AdbPath { get; init; }

    public string? XcodePath { get; init; }

    /// <summary>
    /// Optional AXe executable used when the resident CoreSimulator accessibility bridge is unavailable.
    /// Defaults to <c>ANSIGHT_AXE_PATH</c>, then searches <c>PATH</c> and common Homebrew locations.
    /// </summary>
    public string? IosSimulatorAxePath { get; init; }

    /// <summary>
    /// Optional direct WebDriverAgent server used as an iOS accessibility fallback without Appium.
    /// Defaults to <c>ANSIGHT_WDA_SERVER_URL</c>. When unset, Ansight does not probe port 8100.
    /// </summary>
    public string? IosWebDriverAgentServerUrl { get; init; }

    /// <summary>
    /// Appium server used for physical iOS input through the XCUITest driver.
    /// Defaults to <c>ANSIGHT_APPIUM_SERVER_URL</c> or <c>http://127.0.0.1:4723/</c>.
    /// </summary>
    public string? IosPhysicalDeviceAppiumServerUrl { get; init; }

    public string? IosPhysicalDeviceXcodeTeamId { get; init; }

    public string? IosPhysicalDeviceXcodeSigningIdentity { get; init; }

    public string? IosPhysicalDeviceWebDriverAgentBundleIdentifier { get; init; }

    public string? IosPhysicalDeviceXcodeConfigurationFilePath { get; init; }

    public int? DiscoveryPort { get; init; }

    public int? WebSocketPort { get; init; }

    public int? WebSocketSessionPortRangeStart { get; init; }

    public int? WebSocketSessionPortRangeEnd { get; init; }

    /// <summary>
    /// Enables repository-owned automations for apps registered with a trusted repository path.
    /// </summary>
    public bool EnableRepositoryAutomations { get; init; }

    /// <summary>
    /// Repository roots containing visible <c>ansight/triggers</c> modules for embedded hosts.
    /// </summary>
    public IReadOnlyList<string> AutomationRepositoryPaths { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Node.js runtime used to execute matched repository automations.
    /// </summary>
    public string JavaScriptExecutablePath { get; init; } = "node";

    /// <summary>
    /// Maximum number of matched runs waiting for action execution.
    /// </summary>
    public int AutomationQueueCapacity { get; init; } = 256;

    /// <summary>
    /// Maximum number of repository automation actions that may execute concurrently.
    /// </summary>
    public int AutomationMaxConcurrentRuns { get; init; } = 2;

    /// <summary>
    /// Default bound around only the exported TypeScript trigger function.
    /// Module loading happens before this timer starts.
    /// </summary>
    public TimeSpan AutomationDefaultFunctionTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Default timeout for the host action returned by a trigger function.
    /// </summary>
    public TimeSpan AutomationDefaultActionTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
