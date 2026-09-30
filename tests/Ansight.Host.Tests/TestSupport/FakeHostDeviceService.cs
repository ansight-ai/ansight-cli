namespace Ansight.Host.Tests.TestSupport;

internal sealed class FakeHostDeviceService : IDeviceService
{
    private readonly Dictionary<string, IReadOnlyList<InstalledApplication>> applications =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationChecksum?> applicationChecksums =
        new(StringComparer.OrdinalIgnoreCase);

    public DeviceInventory Inventory { get; set; } = new([], [], []);

    public List<string> StartedDeviceIdentifiers { get; } = [];

    public List<string> ShownDeviceIdentifiers { get; } = [];

    public DeviceStartOptions? StartOptions { get; private set; }

    public string? InstalledDeviceIdentifier { get; private set; }

    public string? InstalledApplicationPath { get; private set; }

    public bool InstalledApplicationPathExisted { get; private set; }

    public string? LaunchedDeviceIdentifier { get; private set; }

    public string? LaunchedApplicationIdentifier { get; private set; }

    public ApplicationLaunchOptions? LaunchOptions { get; private set; }

    public string? StartedSerial { get; set; }

    public string? PhysicalIosProcessIdentity { get; set; }

    public DeviceOperationResult? LaunchResult { get; set; }

    public DeviceOperationResult? TerminateResult { get; set; }

    public string? TerminatedDeviceIdentifier { get; private set; }

    public string? TerminatedApplicationIdentifier { get; private set; }

    public List<string> Operations { get; } = [];

    public void SetApplications(
        string deviceIdentifier,
        params InstalledApplication[] installedApplications)
        => applications[deviceIdentifier] = installedApplications;

    public void SetApplicationChecksum(
        string deviceIdentifier,
        string applicationIdentifier,
        ApplicationChecksum? checksum)
        => applicationChecksums[BuildApplicationKey(deviceIdentifier, applicationIdentifier)] = checksum;

    public Task<DeviceInventory> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Inventory);

    public Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
        => StartAsync(platform, deviceIdentifier, new DeviceStartOptions(), cancellationToken);

    public Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        DeviceStartOptions options,
        CancellationToken cancellationToken = default)
    {
        StartOptions = options;
        StartedDeviceIdentifiers.Add(deviceIdentifier);
        return Task.FromResult(DeviceOperationResult.Success(
            "start",
            platform,
            StartedSerial ?? deviceIdentifier,
            "Started."));
    }

    public Task<DeviceOperationResult> ShowWindowAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        ShownDeviceIdentifiers.Add(deviceIdentifier);
        return Task.FromResult(DeviceOperationResult.Success(
            "show-window",
            platform,
            deviceIdentifier,
            "Shown."));
    }

    public Task<IReadOnlyList<InstalledApplication>> ListApplicationsAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
        => Task.FromResult(applications.GetValueOrDefault(deviceIdentifier) ?? []);

    public Task<string?> GetPhysicalIosProcessIdentityAsync(
        string deviceIdentifier, string applicationIdentifier, string bundlePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PhysicalIosProcessIdentity);

    public Task<ApplicationChecksum?> GetInstalledApplicationChecksumAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
        => Task.FromResult(applicationChecksums.GetValueOrDefault(
            BuildApplicationKey(deviceIdentifier, applicationIdentifier)));

    public Task<DeviceOperationResult> InstallApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationPath,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("install");
        InstalledDeviceIdentifier = deviceIdentifier;
        InstalledApplicationPath = applicationPath;
        InstalledApplicationPathExisted = File.Exists(applicationPath) || Directory.Exists(applicationPath);
        return Task.FromResult(DeviceOperationResult.Success(
            "install-app",
            platform,
            deviceIdentifier,
            "Installed."));
    }

    public Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("launch");
        LaunchedDeviceIdentifier = deviceIdentifier;
        LaunchedApplicationIdentifier = applicationIdentifier;
        return Task.FromResult(LaunchResult ?? DeviceOperationResult.Success(
            "launch-app",
            platform,
            deviceIdentifier,
            "Launched."));
    }

    public Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        ApplicationLaunchOptions launchOptions,
        CancellationToken cancellationToken = default)
    {
        LaunchOptions = launchOptions;
        return LaunchApplicationAsync(
            platform,
            deviceIdentifier,
            applicationIdentifier,
            cancellationToken);
    }

    public Task<DeviceOperationResult> TerminateApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
    {
        Operations.Add("terminate");
        TerminatedDeviceIdentifier = deviceIdentifier;
        TerminatedApplicationIdentifier = applicationIdentifier;
        return Task.FromResult(TerminateResult ?? DeviceOperationResult.Success(
            "terminate-app",
            platform,
            deviceIdentifier,
            "Terminated."));
    }

    private static string BuildApplicationKey(string deviceIdentifier, string applicationIdentifier)
        => $"{deviceIdentifier}\n{applicationIdentifier}";
}
