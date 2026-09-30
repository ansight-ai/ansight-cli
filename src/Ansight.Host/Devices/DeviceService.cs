using System.Globalization;
using System.Xml.Linq;
using Ansight.Adb;
using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;
using Ansight.MacSimulatorHid;
using Ansight.SimCtl;
using SkiaSharp;

namespace Ansight.Host.Devices;

public sealed class DeviceService : IDeviceService, IDeviceLocationService
{
    private const string AndroidEnrollmentIntentExtra = "ai.ansight.bootstrap.payload";
    private const string IosEnrollmentEnvironmentVariable = "ANSIGHT_ENROLLMENT_PAYLOAD";
    internal const string IosSimulatorHidCapabilityName = "ios.simulator-hid";
    private readonly IosAudioRouteRecovery? iosAudioRecovery;
    private readonly string? configuredAdbPath;
    private readonly string? configuredXcodePath;
    private readonly Task<IosDeviceCapabilities> iosDeviceCapabilitiesTask;
    private readonly Lock deviceKindGate = new();
    private readonly Lock physicalIosProcessGate = new();
    private readonly Dictionary<string, string> knownDeviceKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> knownDevicePlatforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimCtlDevice> knownIosSimulators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PhysicalIosApplicationTarget, int> physicalIosProcessIds = [];

    public DeviceService(RuntimeOptions options)
        : this(options, MacSimulatorHidCompatibilityProbe.Check)
    {
    }

    internal DeviceService(RuntimeOptions options, IosAudioRouteRecovery iosAudioRecovery)
        : this(options)
    {
        this.iosAudioRecovery = iosAudioRecovery;
    }

    internal DeviceService(
        RuntimeOptions options,
        Func<string, MacSimulatorHidCompatibility> simulatorHidProbe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(simulatorHidProbe);
        configuredAdbPath = NormalizeOptionalPath(options.AdbPath);
        configuredXcodePath = NormalizeOptionalPath(options.XcodePath);
        iosDeviceCapabilitiesTask = ResolveIosDeviceCapabilitiesAsync(
            configuredXcodePath,
            simulatorHidProbe);
    }

    internal DeviceService(
        RuntimeOptions options,
        DeviceCapability simulatorHidCapability)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(simulatorHidCapability);
        configuredAdbPath = NormalizeOptionalPath(options.AdbPath);
        configuredXcodePath = NormalizeOptionalPath(options.XcodePath);
        iosDeviceCapabilitiesTask = Task.FromResult(new IosDeviceCapabilities(
            SimCtlToolResolution.NotFound("SimCtl resolution was not supplied by the test."),
            simulatorHidCapability));
    }

    public async Task<IReadOnlyList<DeviceCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var adb = AdbToolLocator.Resolve(configuredAdbPath);
        var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : configuredAdbPath);
        var iosCapabilities = await iosDeviceCapabilitiesTask
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var simCtl = iosCapabilities.SimCtl;
        return
        [
            new DeviceCapability(
                DevicePlatforms.Android,
                adb.IsFound,
                adb.IsFound ? adb.AdbPath : string.Empty,
                adb.IsFound
                    ? emulator.IsFound
                        ? $"ADB and Android Emulator are available. Emulator: {emulator.EmulatorPath}"
                        : $"ADB is available. {emulator.Message}"
                    : adb.Message),
            new DeviceCapability(
                DevicePlatforms.Ios,
                simCtl.IsFound,
                simCtl.IsFound ? simCtl.SimCtlPath : string.Empty,
                simCtl.IsFound
                    ? simCtl.Message
                      + " Physical iOS lifecycle uses xcrun devicectl; physical-device UI input uses Appium with XCUITest."
                    : simCtl.Message),
            iosCapabilities.SimulatorHid
        ];
    }

    internal DeviceCapability GetIosSimulatorHidCapability()
        => iosDeviceCapabilitiesTask.GetAwaiter().GetResult().SimulatorHid;

    internal async Task<HeadlessIosScreenshotSize> GetIosSimulatorScreenshotSizeAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
        var screenshot = await simCtl.CaptureScreenshotJpegAsync(deviceIdentifier, cancellationToken)
            .ConfigureAwait(false);
        using var data = SKData.CreateCopy(screenshot);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new InvalidOperationException(
                "The iOS Simulator screenshot did not contain usable display dimensions.");
        }

        return new HeadlessIosScreenshotSize(codec.Info.Width, codec.Info.Height);
    }

    internal static DeviceCapability CreateIosSimulatorHidCapability(
        SimCtlToolResolution simCtl,
        Func<string, MacSimulatorHidCompatibility> simulatorHidProbe)
    {
        ArgumentNullException.ThrowIfNull(simCtl);
        ArgumentNullException.ThrowIfNull(simulatorHidProbe);
        if (!simCtl.IsFound)
        {
            return new DeviceCapability(
                IosSimulatorHidCapabilityName,
                false,
                string.Empty,
                $"Native SimulatorKit HID compatibility was not checked because {simCtl.Message}",
                "not-checked");
        }

        var compatibility = simulatorHidProbe(simCtl.DeveloperDirectory);
        return new DeviceCapability(
            IosSimulatorHidCapabilityName,
            compatibility.IsAvailable,
            compatibility.DeveloperDirectory ?? simCtl.DeveloperDirectory,
            compatibility.Message,
            compatibility.Status);
    }

    private static async Task<IosDeviceCapabilities> ResolveIosDeviceCapabilitiesAsync(
        string? configuredXcodePath,
        Func<string, MacSimulatorHidCompatibility> simulatorHidProbe)
    {
        SimCtlToolResolution simCtl;
        try
        {
            simCtl = await SimCtlToolLocator.ResolveAsync(configuredXcodePath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception)
        {
            simCtl = SimCtlToolResolution.NotFound(
                $"Xcode tool discovery failed: {exception.Message}");
        }

        return new IosDeviceCapabilities(
            simCtl,
            CreateIosSimulatorHidCapability(simCtl, simulatorHidProbe));
    }

    public async Task<DeviceInventory> ListAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<DeviceDescriptor>();
        var warnings = new List<string>();
        var capabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        var androidCapability = capabilities.First(capability =>
            string.Equals(capability.Platform, DevicePlatforms.Android, StringComparison.Ordinal));
        if (androidCapability.IsAvailable)
        {
            try
            {
                var adb = CreateAdbClient();
                var connectedDevices = await adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var device in connectedDevices)
                {
                    var isEmulator = device.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase);
                    string? avdName = null;
                    string? formFactor = null;
                    if (isEmulator && device.IsConnected)
                    {
                        try
                        {
                            avdName = await adb.GetEmulatorAvdNameAsync(device.Serial, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidOperationException)
                        {
                            warnings.Add($"Could not resolve the AVD name for '{device.Serial}': {exception.Message}");
                        }
                    }

                    if (device.IsConnected)
                    {
                        try
                        {
                            formFactor = MapAndroidFormFactor(
                                await adb.GetDeviceFormFactorAsync(device.Serial, cancellationToken)
                                    .ConfigureAwait(false));
                        }
                        catch (Exception exception) when (exception is IOException or InvalidOperationException)
                        {
                            warnings.Add(
                                $"Could not determine the form factor for '{device.Serial}': {exception.Message}");
                        }
                    }

                    devices.Add(new DeviceDescriptor(
                        device.Serial,
                        FirstNonEmpty(avdName, device.Model, device.Device, device.Product, device.Serial),
                        DevicePlatforms.Android,
                        device.Product ?? string.Empty,
                        device.State,
                        device.IsConnected,
                        device.IsConnected,
                        isEmulator ? "emulator" : "device")
                    {
                        FormFactor = formFactor
                    });
                }

                var emulatorResolution = AndroidEmulatorToolLocator.Resolve(adb.AdbPath);
                if (emulatorResolution.IsFound)
                {
                    var virtualDeviceClient = new AndroidVirtualDeviceClient(emulatorResolution);
                    var avdNames = await virtualDeviceClient.ListAsync(cancellationToken).ConfigureAwait(false);
                    var connectedNames = devices
                        .Where(static device => device.Platform == DevicePlatforms.Android)
                        .Select(static device => device.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    devices.AddRange(avdNames
                        .Where(name => !connectedNames.Contains(name))
                        .Select(name => new DeviceDescriptor(
                            name,
                            name,
                            DevicePlatforms.Android,
                            string.Empty,
                            "shutdown",
                            false,
                            true,
                            "avd")
                        {
                            FormFactor = MapAndroidFormFactor(
                                virtualDeviceClient.GetDeviceFormFactor(name))
                        }));
                }
                else
                {
                    warnings.Add(emulatorResolution.Message);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                warnings.Add(exception.Message);
            }
        }
        else
        {
            warnings.Add(androidCapability.Message);
        }

        var iosCapability = capabilities.First(capability =>
            string.Equals(capability.Platform, DevicePlatforms.Ios, StringComparison.Ordinal));
        if (iosCapability.IsAvailable)
        {
            try
            {
                var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                var simulators = await simCtl.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                lock (deviceKindGate)
                {
                    foreach (var simulator in simulators)
                    {
                        knownIosSimulators[simulator.Udid] = simulator;
                    }
                }
                devices.AddRange(simulators.Select(static device => new DeviceDescriptor(
                    device.Udid,
                    device.Name,
                    DevicePlatforms.Ios,
                    device.RuntimeIdentifier,
                    device.State,
                    device.IsBooted,
                    device.IsAvailable,
                    "simulator")
                {
                    FormFactor = ResolveAppleFormFactor(device.Name, productType: null)
                }));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                warnings.Add(exception.Message);
            }

            try
            {
                var coreDevice = await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false);
                var physicalDevices = await coreDevice.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
                devices.AddRange(physicalDevices
                    .Where(static device => device.IsIos)
                    .Select(static device => new DeviceDescriptor(
                        device.Identifier,
                        device.Name,
                        DevicePlatforms.Ios,
                        string.Join(' ', new[] { device.ProductType, device.OperatingSystemVersion }
                            .Where(static value => !string.IsNullOrWhiteSpace(value))),
                        device.State,
                        device.IsAvailable,
                        device.IsAvailable,
                        DeviceKinds.Device)
                    {
                        FormFactor = ResolveAppleFormFactor(device.Name, device.ProductType)
                    }));
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidOperationException
                                               or AudioInjectionException
            or PlatformNotSupportedException
                                               or TimeoutException)
            {
                warnings.Add($"Physical iOS device discovery failed: {exception.Message}");
            }
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            warnings.Add(iosCapability.Message);
        }

        var orderedDevices = devices
            .OrderBy(static device => device.Platform, StringComparer.Ordinal)
            .ThenByDescending(static device => device.IsBooted)
            .ThenBy(static device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (deviceKindGate)
        {
            foreach (var device in orderedDevices)
            {
                knownDeviceKinds[device.Identifier] = device.Kind;
                knownDevicePlatforms[device.Identifier] = device.Platform;
                if (device.Platform == DevicePlatforms.Android &&
                    device.IsVirtual &&
                    !string.IsNullOrWhiteSpace(device.Name))
                {
                    knownDeviceKinds[device.Name] = device.Kind;
                    knownDevicePlatforms[device.Name] = device.Platform;
                }
            }
        }

        return new DeviceInventory(
            capabilities,
            orderedDevices,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
        => await StartAsync(
            platform,
            deviceIdentifier,
            new DeviceStartOptions(),
            cancellationToken).ConfigureAwait(false);

    public async Task<DeviceOperationResult> StartAsync(
        string platform,
        string deviceIdentifier,
        DeviceStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var headless = options.Headless || DeviceLaunchContext.Headless;
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        try
        {
            if (normalizedPlatform == DevicePlatforms.Android)
            {
                var adbResolution = AdbToolLocator.Resolve(configuredAdbPath);
                var emulatorResolution = AndroidEmulatorToolLocator.Resolve(
                    adbResolution.IsFound ? adbResolution.AdbPath : configuredAdbPath);
                if (!emulatorResolution.IsFound)
                {
                    return Failure("start", normalizedPlatform, normalizedIdentifier, emulatorResolution.Message);
                }

                var processId = await new AndroidVirtualDeviceClient(emulatorResolution)
                    .LaunchAsync(
                        normalizedIdentifier,
                        new AndroidVirtualDeviceLaunchOptions(Headless: headless),
                        cancellationToken)
                    .ConfigureAwait(false);
                var serial = await CreateAdbClient().WaitForEmulatorBootAsync(
                    normalizedIdentifier, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                return Success(
                    "start",
                    normalizedPlatform,
                    serial,
                    $"Android virtual device '{normalizedIdentifier}' booted as '{serial}' with process ID {processId}.");
            }

            if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
            {
                return Success(
                    "start",
                    normalizedPlatform,
                    normalizedIdentifier,
                    "Physical iOS device is connected and ready.");
            }

            var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
            await StartSimulatorAsync(simCtl, normalizedIdentifier, headless, cancellationToken).ConfigureAwait(false);
            return Success("start", normalizedPlatform, normalizedIdentifier, "iOS Simulator booted.");
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            return Failure("start", normalizedPlatform, normalizedIdentifier, exception.Message);
        }
    }

    internal static async Task StartSimulatorAsync(
        SimCtlClient simCtl,
        string deviceIdentifier,
        bool headless,
        CancellationToken cancellationToken)
    {
        await simCtl.BootAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        await simCtl.WaitForBootAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (!headless)
        {
            await simCtl.ShowSimulatorAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DeviceOperationResult> ShowWindowAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        if (DeviceLaunchContext.Headless)
        {
            return Success("show-window", normalizedPlatform, normalizedIdentifier, "Headless launch; window left unchanged.");
        }

        try
        {
            if (normalizedPlatform == DevicePlatforms.Android)
            {
                return Success(
                    "show-window",
                    normalizedPlatform,
                    normalizedIdentifier,
                    "The Android emulator window is managed by its running emulator process.");
            }

            if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
            {
                return Success(
                    "show-window",
                    normalizedPlatform,
                    normalizedIdentifier,
                    "Physical iOS devices do not have a host simulator window.");
            }

            var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
            await simCtl.ShowSimulatorAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false);
            return Success(
                "show-window",
                normalizedPlatform,
                normalizedIdentifier,
                "iOS Simulator window shown.");
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            return Failure(
                "show-window",
                normalizedPlatform,
                normalizedIdentifier,
                exception.Message);
        }
    }

    public async Task<DeviceOperationResult> ShutdownAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            "shutdown",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    var result = await CreateAdbClient().RunAsync(
                        ["-s", normalizedIdentifier, "emu", "kill"],
                        cancellationToken).ConfigureAwait(false);
                    EnsureAdbSuccess("shut down the Android emulator", result);
                    return "Android emulator shutdown requested.";
                }

                if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                {
                    throw new PlatformNotSupportedException(
                        "Ansight does not shut down physical iOS devices. Disconnect the device when it is no longer needed.");
                }

                var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                await simCtl.ShutdownAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false);
                return "iOS Simulator shut down.";
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstalledApplication>> ListApplicationsAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            var result = await CreateAdbClient().RunAsync(
                ["-s", normalizedIdentifier, "shell", "pm", "list", "packages", "-3"],
                cancellationToken).ConfigureAwait(false);
            EnsureAdbSuccess("list installed Android applications", result);
            return result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static line => line.StartsWith("package:", StringComparison.Ordinal)
                    ? line["package:".Length..].Trim()
                    : line.Trim())
                .Where(static identifier => !string.IsNullOrWhiteSpace(identifier))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(static identifier => new InstalledApplication(identifier, identifier))
                .ToArray();
        }

        if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
        {
            return await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
                .GetInstalledApplicationsAsync(normalizedIdentifier, cancellationToken)
                .ConfigureAwait(false);
        }

        var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
        var knownSimulator = GetKnownIosSimulator(normalizedIdentifier);
        if (knownSimulator is { IsBooted: false } && !string.IsNullOrWhiteSpace(knownSimulator.DataPath))
        {
            return await ReadInstalledSimulatorApplicationsAsync(knownSimulator.DataPath, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<SimCtlInstalledApplication> applications;
        try
        {
            applications = await simCtl.GetInstalledApplicationsAsync(normalizedIdentifier, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var simulator = (await simCtl.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(device => string.Equals(
                    device.Udid,
                    normalizedIdentifier,
                    StringComparison.OrdinalIgnoreCase));
            if (simulator is null || simulator.IsBooted || string.IsNullOrWhiteSpace(simulator.DataPath))
            {
                throw;
            }

            lock (deviceKindGate)
            {
                knownIosSimulators[simulator.Udid] = simulator;
            }
            return await ReadInstalledSimulatorApplicationsAsync(simulator.DataPath, cancellationToken)
                .ConfigureAwait(false);
        }
        return applications
            .Select(static application => new InstalledApplication(
                application.BundleIdentifier,
                FirstNonEmpty(application.DisplayName, application.BundleName, application.BundleIdentifier),
                application.BundlePath,
                application.Version,
                application.BuildVersion,
                ResolveBundleInstalledAtUtc(application.BundlePath)))
            .OrderBy(static application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string?> GetPhysicalIosProcessIdentityAsync(
        string deviceIdentifier, string applicationIdentifier, string bundlePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        var normalizedApp = NormalizeRequired(applicationIdentifier, nameof(applicationIdentifier));
        if (!await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
            throw new ArgumentException("The selected device is not a physical iOS device.", nameof(deviceIdentifier));
        return await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
            .GetRunningApplicationProcessIdentityAsync(normalizedIdentifier, normalizedApp, bundlePath, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InstalledApplication?> GetInstalledApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        var normalizedApplicationIdentifier = NormalizeRequired(
            applicationIdentifier,
            nameof(applicationIdentifier));
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            var application = await CreateAdbClient().GetInstalledApplicationAsync(
                    normalizedIdentifier,
                    normalizedApplicationIdentifier,
                    cancellationToken)
                .ConfigureAwait(false);
            return application is null
                ? null
                : new InstalledApplication(
                    application.PackageIdentifier,
                    application.PackageIdentifier,
                    application.PackagePath,
                    application.Version,
                    application.BuildVersion,
                    application.InstalledAtUtc,
                    application.LastUpdatedAtUtc);
        }

        return (await ListApplicationsAsync(
                normalizedPlatform,
                normalizedIdentifier,
                cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(application => string.Equals(
                application.Identifier,
                normalizedApplicationIdentifier,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<IReadOnlyList<InstalledApplication>> ReadInstalledSimulatorApplicationsAsync(
        string simulatorDataPath,
        CancellationToken cancellationToken = default)
    {
        var applicationsRoot = Path.Combine(
            simulatorDataPath,
            "Containers",
            "Bundle",
            "Application");
        if (!Directory.Exists(applicationsRoot))
        {
            return [];
        }

        var applications = new List<InstalledApplication>();
        foreach (var containerPath in Directory.EnumerateDirectories(applicationsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var applicationPath in Directory.EnumerateDirectories(
                         containerPath,
                         "*.app",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var infoPlistPath = Path.Combine(applicationPath, "Info.plist");
                if (!File.Exists(infoPlistPath))
                {
                    continue;
                }

                try
                {
                    var data = await File.ReadAllBytesAsync(infoPlistPath, cancellationToken).ConfigureAwait(false);
                    var values = ReadPropertyListStrings(data);
                    if (!values.TryGetValue("CFBundleIdentifier", out var identifier)
                        || string.IsNullOrWhiteSpace(identifier))
                    {
                        continue;
                    }

                    values.TryGetValue("CFBundleDisplayName", out var displayName);
                    values.TryGetValue("CFBundleName", out var bundleName);
                    values.TryGetValue("CFBundleShortVersionString", out var version);
                    values.TryGetValue("CFBundleVersion", out var buildVersion);
                    applications.Add(new InstalledApplication(
                        identifier.Trim(),
                        FirstNonEmpty(
                            displayName,
                            bundleName,
                            Path.GetFileNameWithoutExtension(applicationPath),
                            identifier),
                        applicationPath,
                        version,
                        buildVersion,
                        ResolveBundleInstalledAtUtc(applicationPath)));
                }
                catch (Exception exception) when (exception is InvalidDataException
                                                   or InvalidOperationException
                                                   or IOException
                                                   or UnauthorizedAccessException)
                {
                    // A stale or partially removed app container should not hide the other installed apps.
                }
            }
        }

        return applications
            .DistinctBy(static application => application.Identifier, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SimCtlDevice? GetKnownIosSimulator(string deviceIdentifier)
    {
        lock (deviceKindGate)
        {
            return knownIosSimulators.GetValueOrDefault(deviceIdentifier);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadPropertyListStrings(ReadOnlyMemory<byte> data)
    {
        var preview = PropertyListParser.Parse(data);
        var document = XDocument.Parse(preview.SourceText, LoadOptions.None);
        var dictionary = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "dict");
        var elements = dictionary?.Elements().ToArray() ?? [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name.LocalName == "key"
                && elements[index + 1].Name.LocalName == "string")
            {
                values[elements[index].Value] = elements[index + 1].Value;
            }
        }

        return values;
    }

    public async Task<ApplicationChecksum?> GetInstalledApplicationChecksumAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        var normalizedApplicationIdentifier = NormalizeRequired(
            applicationIdentifier,
            nameof(applicationIdentifier));
        if (normalizedPlatform != DevicePlatforms.Android)
        {
            return null;
        }

        var checksum = await CreateAdbClient().GetInstalledPackageSha256Async(
            normalizedIdentifier,
            normalizedApplicationIdentifier,
            cancellationToken).ConfigureAwait(false);
        return checksum is null ? null : new ApplicationChecksum(checksum);
    }

    public Task<DeviceOperationResult> InstallApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationPath,
        CancellationToken cancellationToken = default)
    {
        var fullApplicationPath = Path.GetFullPath(
            NormalizeRequired(applicationPath, nameof(applicationPath)));
        return ExecuteAsync(
            "install-app",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    await CreateAdbClient().InstallPackageAsync(
                        normalizedIdentifier,
                        fullApplicationPath,
                        cancellationToken).ConfigureAwait(false);
                    return $"Installed '{fullApplicationPath}'.";
                }

                if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                {
                    await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
                        .InstallApplicationAsync(normalizedIdentifier, fullApplicationPath, cancellationToken)
                        .ConfigureAwait(false);
                    return $"Installed '{fullApplicationPath}' on the physical iOS device.";
                }

                var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                await simCtl.InstallApplicationAsync(
                    normalizedIdentifier,
                    fullApplicationPath,
                    cancellationToken).ConfigureAwait(false);
                return $"Installed '{fullApplicationPath}'.";
            });
    }

    public Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
        => LaunchApplicationCoreAsync(
            platform,
            deviceIdentifier,
            applicationIdentifier,
            launchOptions: null,
            cancellationToken: cancellationToken);

    public Task<DeviceOperationResult> LaunchApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        ApplicationLaunchOptions launchOptions,
        CancellationToken cancellationToken = default)
        => LaunchApplicationCoreAsync(
            platform,
            deviceIdentifier,
            applicationIdentifier,
            launchOptions ?? throw new ArgumentNullException(nameof(launchOptions)),
            cancellationToken: cancellationToken);

    private Task<DeviceOperationResult> LaunchApplicationCoreAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        ApplicationLaunchOptions? launchOptions,
        CancellationToken cancellationToken)
    {
        var normalizedApplicationIdentifier = NormalizeRequired(
            applicationIdentifier,
            nameof(applicationIdentifier));
        var enrollmentPayload = string.IsNullOrWhiteSpace(launchOptions?.AnsightEnrollmentPayload)
            ? null
            : launchOptions.AnsightEnrollmentPayload.Trim();
        return ExecuteAsync(
            "launch-app",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                var audioRouteRecovered = false;
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    var adb = CreateAdbClient();
                    if (enrollmentPayload is null)
                    {
                        await adb.LaunchApplicationAsync(
                            normalizedIdentifier,
                            normalizedApplicationIdentifier,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await adb.LaunchApplicationAsync(
                            normalizedIdentifier,
                            normalizedApplicationIdentifier,
                            AndroidEnrollmentIntentExtra,
                            enrollmentPayload,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                    {
                        var processId = await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
                            .LaunchApplicationAsync(
                                normalizedIdentifier,
                                normalizedApplicationIdentifier,
                                enrollmentPayload is null
                                    ? null
                                    : new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        [IosEnrollmentEnvironmentVariable] = enrollmentPayload
                                    },
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (processId.HasValue)
                        {
                            lock (physicalIosProcessGate)
                            {
                                physicalIosProcessIds[new PhysicalIosApplicationTarget(
                                    normalizedIdentifier,
                                    normalizedApplicationIdentifier)] = processId.Value;
                            }
                        }
                    }
                    else
                    {
                        var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                        if (iosAudioRecovery is not null)
                        {
                            audioRouteRecovered = await iosAudioRecovery.RecoverBeforeLaunchAsync(
                                normalizedIdentifier,
                                normalizedApplicationIdentifier,
                                async token => (await simCtl.GetDevicesAsync(token).ConfigureAwait(false))
                                    .Single(device => string.Equals(device.Udid, normalizedIdentifier, StringComparison.OrdinalIgnoreCase))
                                    .LastBootedUtc,
                                async token =>
                                {
                                    var inventory = await simCtl.GetDevicesAsync(token).ConfigureAwait(false);
                                    if (inventory.Any(device => device.IsBooted
                                        && !string.Equals(device.Udid, normalizedIdentifier, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        throw new InvalidOperationException("Simulator audio route is stale—restart required. Stop other booted simulators before starting a fresh run.");
                                    }

                                    var target = inventory.Single(device => string.Equals(device.Udid, normalizedIdentifier, StringComparison.OrdinalIgnoreCase));
                                    if (target.IsBooted)
                                        await simCtl.ShutdownAsync(normalizedIdentifier, token).ConfigureAwait(false);
                                    await simCtl.BootAsync(normalizedIdentifier, token).ConfigureAwait(false);
                                    await simCtl.WaitForBootAsync(normalizedIdentifier, token).ConfigureAwait(false);
                                    if (!DeviceLaunchContext.Headless)
                                        await simCtl.ShowSimulatorAsync(normalizedIdentifier, token).ConfigureAwait(false);
                                },
                                cancellationToken).ConfigureAwait(false);
                        }

                        await simCtl.LaunchApplicationAsync(
                            normalizedIdentifier,
                            normalizedApplicationIdentifier,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return audioRouteRecovered
                    ? $"Restarted the simulator to recover its stale audio route, then launched '{normalizedApplicationIdentifier}'."
                    : $"Launched '{normalizedApplicationIdentifier}'.";
            });
    }

    public Task<DeviceOperationResult> TerminateApplicationAsync(
        string platform,
        string deviceIdentifier,
        string applicationIdentifier,
        CancellationToken cancellationToken = default)
    {
        var normalizedApplicationIdentifier = NormalizeRequired(
            applicationIdentifier,
            nameof(applicationIdentifier));
        return ExecuteAsync(
            "terminate-app",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    await CreateAdbClient().TerminateApplicationAsync(
                        normalizedIdentifier,
                        normalizedApplicationIdentifier,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                    {
                        var target = new PhysicalIosApplicationTarget(
                            normalizedIdentifier,
                            normalizedApplicationIdentifier);
                        int? processId;
                        lock (physicalIosProcessGate)
                        {
                            processId = physicalIosProcessIds.TryGetValue(target, out var knownProcessId)
                                ? knownProcessId
                                : null;
                        }

                        await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
                            .TerminateApplicationAsync(
                                normalizedIdentifier,
                                normalizedApplicationIdentifier,
                                processId,
                                cancellationToken)
                            .ConfigureAwait(false);
                        lock (physicalIosProcessGate)
                        {
                            physicalIosProcessIds.Remove(target);
                        }
                    }
                    else
                    {
                        var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                        await simCtl.TerminateApplicationAsync(
                            normalizedIdentifier,
                            normalizedApplicationIdentifier,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return $"Terminated '{normalizedApplicationIdentifier}'.";
            });
    }

    public Task<DeviceOperationResult> SetLocationAsync(
        string platform,
        string deviceIdentifier,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "set-location",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    await CreateAdbClient().SetEmulatorLocationAsync(
                        normalizedIdentifier,
                        latitude,
                        longitude,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                    {
                        throw new PlatformNotSupportedException(
                            "Host location simulation is unavailable for physical iOS devices.");
                    }

                    var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                    await simCtl.SetLocationAsync(
                        normalizedIdentifier,
                        latitude,
                        longitude,
                        cancellationToken).ConfigureAwait(false);
                }

                return $"Location set to {latitude.ToString("R", CultureInfo.InvariantCulture)}, "
                       + longitude.ToString("R", CultureInfo.InvariantCulture) + ".";
            });

    public Task<DeviceOperationResult> ClearLocationAsync(
        string platform,
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "clear-location",
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform == DevicePlatforms.Android)
                {
                    throw new PlatformNotSupportedException(
                        "Android Emulator does not expose a reliable clear-location operation; set a new location instead.");
                }

                if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                {
                    throw new PlatformNotSupportedException(
                        "Host location simulation is unavailable for physical iOS devices.");
                }

                var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                await simCtl.ClearLocationAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false);
                return "Simulated location cleared.";
            });

    public Task<DeviceOperationResult> SendTapAsync(
        string platform,
        string deviceIdentifier,
        int x,
        int y,
        CancellationToken cancellationToken = default)
        => ExecuteAndroidInputAsync(
            "tap",
            platform,
            deviceIdentifier,
            ["input", "tap", x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture)],
            cancellationToken);

    public Task<DeviceOperationResult> SendSwipeAsync(
        string platform,
        string deviceIdentifier,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
        => ExecuteAndroidInputAsync(
            "swipe",
            platform,
            deviceIdentifier,
            [
                "input",
                "swipe",
                startX.ToString(CultureInfo.InvariantCulture),
                startY.ToString(CultureInfo.InvariantCulture),
                endX.ToString(CultureInfo.InvariantCulture),
                endY.ToString(CultureInfo.InvariantCulture),
                Math.Clamp(durationMilliseconds, 1, 60_000).ToString(CultureInfo.InvariantCulture)
            ],
            cancellationToken);

    public Task<DeviceOperationResult> SendTextAsync(
        string platform,
        string deviceIdentifier,
        string value,
        CancellationToken cancellationToken = default)
        => ExecuteAndroidInputAsync(
            "type-text",
            platform,
            deviceIdentifier,
            ["input", "text", NormalizeRequired(value, nameof(value)).Replace(" ", "%s", StringComparison.Ordinal)],
            cancellationToken);

    public Task<DeviceOperationResult> SendButtonAsync(
        string platform,
        string deviceIdentifier,
        string button,
        CancellationToken cancellationToken = default)
    {
        var keyCode = NormalizeRequired(button, nameof(button)).ToLowerInvariant() switch
        {
            "back" => "KEYCODE_BACK",
            "home" => "KEYCODE_HOME",
            "enter" => "KEYCODE_ENTER",
            "menu" => "KEYCODE_MENU",
            "power" => "KEYCODE_POWER",
            "volume-up" => "KEYCODE_VOLUME_UP",
            "volume-down" => "KEYCODE_VOLUME_DOWN",
            _ => throw new ArgumentException($"Unsupported Android button '{button}'.", nameof(button))
        };
        return ExecuteAndroidInputAsync(
            "press-button",
            platform,
            deviceIdentifier,
            ["input", "keyevent", keyCode],
            cancellationToken);
    }

    public async Task<DeviceOperationResult> CaptureScreenshotAsync(
        string platform,
        string deviceIdentifier,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        var fullOutputPath = Path.GetFullPath(NormalizeRequired(outputPath, nameof(outputPath)));
        try
        {
            byte[] bytes;
            if (normalizedPlatform == DevicePlatforms.Android)
            {
                bytes = await CreateAdbClient().CaptureScreenshotPngAsync(
                    normalizedIdentifier,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (await IsPhysicalIosDeviceAsync(normalizedIdentifier, cancellationToken).ConfigureAwait(false))
                {
                    throw new PlatformNotSupportedException(
                        "Use the Ansight SDK or Appium/WebDriverAgent for physical iOS screenshots.");
                }

                var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
                bytes = await simCtl.CaptureScreenshotJpegAsync(
                    normalizedIdentifier,
                    cancellationToken).ConfigureAwait(false);
            }

            var parentPath = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                Directory.CreateDirectory(parentPath);
            }

            await File.WriteAllBytesAsync(fullOutputPath, bytes, cancellationToken).ConfigureAwait(false);
            return Success(
                "screenshot",
                normalizedPlatform,
                normalizedIdentifier,
                $"Screenshot saved to '{fullOutputPath}'.",
                fullOutputPath);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            return Failure("screenshot", normalizedPlatform, normalizedIdentifier, exception.Message);
        }
    }

    private Task<DeviceOperationResult> ExecuteAndroidInputAsync(
        string operation,
        string platform,
        string deviceIdentifier,
        IReadOnlyList<string> inputArguments,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            operation,
            platform,
            deviceIdentifier,
            async (normalizedPlatform, normalizedIdentifier) =>
            {
                if (normalizedPlatform != DevicePlatforms.Android)
                {
                    throw new PlatformNotSupportedException(
                        "Direct DeviceService input supports Android only; route iOS Simulator input through the macOS Simulator HID driver.");
                }

                var arguments = new List<string> { "-s", normalizedIdentifier, "shell" };
                arguments.AddRange(inputArguments);
                var result = await CreateAdbClient().RunAsync(arguments, cancellationToken).ConfigureAwait(false);
                EnsureAdbSuccess($"perform Android input operation '{operation}'", result);
                return $"Android input operation '{operation}' completed.";
            });

    private async Task<DeviceOperationResult> ExecuteAsync(
        string operation,
        string platform,
        string deviceIdentifier,
        Func<string, string, Task<string>> action)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedIdentifier = NormalizeRequired(deviceIdentifier, nameof(deviceIdentifier));
        try
        {
            var message = await action(normalizedPlatform, normalizedIdentifier).ConfigureAwait(false);
            return Success(operation, normalizedPlatform, normalizedIdentifier, message);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            return Failure(operation, normalizedPlatform, normalizedIdentifier, exception.Message);
        }
    }

    private AdbClient CreateAdbClient()
    {
        var resolution = AdbToolLocator.Resolve(configuredAdbPath);
        if (!resolution.IsFound)
        {
            throw new InvalidOperationException(resolution.Message);
        }

        return new AdbClient(resolution.AdbPath);
    }

    private async Task<SimCtlClient> CreateSimCtlClientAsync(CancellationToken cancellationToken)
    {
        var resolution = await SimCtlToolLocator.ResolveAsync(configuredXcodePath, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsFound)
        {
            throw new PlatformNotSupportedException(resolution.Message);
        }

        return new SimCtlClient(resolution);
    }

    private async Task<CoreDeviceClient> CreateCoreDeviceClientAsync(CancellationToken cancellationToken)
    {
        var resolution = await SimCtlToolLocator.ResolveAsync(configuredXcodePath, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsFound)
        {
            throw new PlatformNotSupportedException(resolution.Message);
        }

        return new CoreDeviceClient(resolution);
    }

    private async Task<bool> IsPhysicalIosDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        lock (deviceKindGate)
        {
            if (knownDeviceKinds.TryGetValue(deviceIdentifier, out var knownKind))
            {
                return DeviceKinds.IsPhysical(knownKind);
            }
        }

        Exception? physicalDiscoveryFailure = null;
        try
        {
            var physicalDevices = await (await CreateCoreDeviceClientAsync(cancellationToken).ConfigureAwait(false))
                .GetDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            var isPhysical = physicalDevices.Any(device => string.Equals(
                device.Identifier,
                deviceIdentifier,
                StringComparison.OrdinalIgnoreCase));
            if (isPhysical)
            {
                lock (deviceKindGate)
                {
                    knownDeviceKinds[deviceIdentifier] = DeviceKinds.Device;
                    knownDevicePlatforms[deviceIdentifier] = DevicePlatforms.Ios;
                }

                return true;
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or AudioInjectionException
            or PlatformNotSupportedException
                                           or TimeoutException)
        {
            physicalDiscoveryFailure = exception;
        }

        var simCtl = await CreateSimCtlClientAsync(cancellationToken).ConfigureAwait(false);
        var simulators = await simCtl.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (simulators.Any(device => string.Equals(
                device.Udid,
                deviceIdentifier,
                StringComparison.OrdinalIgnoreCase)))
        {
            lock (deviceKindGate)
            {
                knownDeviceKinds[deviceIdentifier] = DeviceKinds.Simulator;
                knownDevicePlatforms[deviceIdentifier] = DevicePlatforms.Ios;
            }

            return false;
        }

        if (physicalDiscoveryFailure is not null)
        {
            throw new InvalidOperationException(
                $"Could not determine whether '{deviceIdentifier}' is a physical iOS device: "
                + physicalDiscoveryFailure.Message,
                physicalDiscoveryFailure);
        }

        return false;
    }

    internal bool TryGetKnownDevice(
        string deviceIdentifier,
        out string platform,
        out string kind)
    {
        lock (deviceKindGate)
        {
            if (knownDeviceKinds.TryGetValue(deviceIdentifier, out kind!)
                && knownDevicePlatforms.TryGetValue(deviceIdentifier, out platform!))
            {
                return true;
            }
        }

        platform = string.Empty;
        kind = DeviceKinds.Unknown;
        return false;
    }

    private static void EnsureAdbSuccess(string operation, AdbCommandResult result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"ADB failed to {operation} with exit code {result.ExitCode}."
                : $"ADB failed to {operation}: {detail}");
    }

    private static bool IsOperationFailure(Exception exception)
        => exception is ArgumentException
            or IOException
            or InvalidOperationException
            or AudioInjectionException
            or PlatformNotSupportedException
            or TimeoutException
            or UnauthorizedAccessException;

    private static DeviceOperationResult Success(
        string operation,
        string platform,
        string identifier,
        string message,
        string? outputPath = null)
        => DeviceOperationResult.Success(operation, platform, identifier, message, outputPath);

    private static DeviceOperationResult Failure(
        string operation,
        string platform,
        string identifier,
        string message)
        => DeviceOperationResult.Failure(operation, platform, identifier, message);

    private static string NormalizePlatform(string platform)
    {
        var normalized = NormalizeRequired(platform, nameof(platform)).ToLowerInvariant();
        return normalized switch
        {
            "android" => DevicePlatforms.Android,
            "ios" or "iphone" or "simctl" => DevicePlatforms.Ios,
            _ => throw new ArgumentException(
                $"Unsupported device platform '{platform}'. Expected android or ios.",
                nameof(platform))
        };
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string? NormalizeOptionalPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string? MapAndroidFormFactor(AndroidDeviceFormFactor formFactor)
        => formFactor switch
        {
            AndroidDeviceFormFactor.Phone => DeviceFormFactors.Phone,
            AndroidDeviceFormFactor.Tablet => DeviceFormFactors.Tablet,
            _ => null
        };

    internal static string? ResolveAppleFormFactor(string? name, string? productType)
    {
        var family = FirstNonEmpty(productType, name);
        if (family.StartsWith("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceFormFactors.Tablet;
        }

        return family.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)
               || family.StartsWith("iPod", StringComparison.OrdinalIgnoreCase)
            ? DeviceFormFactors.Phone
            : null;
    }

    private static DateTimeOffset? ResolveBundleInstalledAtUtc(string? bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath) || !Directory.Exists(bundlePath))
        {
            return null;
        }

        try
        {
            var containerPath = Directory.GetParent(bundlePath)?.FullName ?? bundlePath;
            var createdUtc = Directory.GetCreationTimeUtc(containerPath);
            return createdUtc <= DateTime.UnixEpoch
                ? null
                : new DateTimeOffset(createdUtc, TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private sealed record IosDeviceCapabilities(
        SimCtlToolResolution SimCtl,
        DeviceCapability SimulatorHid);

    private sealed record PhysicalIosApplicationTarget(
        string DeviceIdentifier,
        string ApplicationIdentifier);
}
