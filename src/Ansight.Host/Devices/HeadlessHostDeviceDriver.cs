using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Adb;
using Ansight.MacSimulatorHid;
using Ansight.RemoteSimulator.Core.Simulator.Apple;

namespace Ansight.Host.Devices;

public sealed partial class HeadlessHostDeviceDriver :
    IDeviceLifecycleDriver,
    IUiAccessibilityDriver,
    IUiInputDriver,
    IUiInputPreflightDriver,
    IDisposable
{
    private const string AndroidBackend = "android-adb";
    private const string AndroidAccessibilityBackend = "android-uiautomator-accessibility";
    private const string IosBackend = "simulator-kit-hid-dynamic-binding";
    private const string IosPhysicalBackend = "appium-xcuitest-webdriveragent";
    private const string IosSimulatorAccessibilityBackend = "ios-core-simulator-ax-service";
    private const string IosWdaAccessibilityBackend = "ios-webdriveragent-accessibility";
    private const string IosAppiumAccessibilityBackend = "appium-xcuitest-webdriveragent-accessibility";
    private const string AndroidAccessibilityDumpPath = "/sdcard/ansight-accessibility.xml";
    private static readonly TimeSpan DeviceResolutionCacheDuration = TimeSpan.FromSeconds(2);
    private readonly DeviceService devices;
    private readonly string? configuredAdbPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> inputGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock deviceResolutionGate = new();
    private readonly Dictionary<string, DeviceResolutionCacheEntry> deviceResolutionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IosPhysicalDeviceAppiumClient iosPhysicalDeviceInput;
    private readonly IosSimulatorAccessibilityClient iosSimulatorAccessibility;
    private readonly IosWebDriverAgentClient? iosWebDriverAgentAccessibility;
    private readonly Lock simulatorHidSessionsGate = new();
    private readonly Dictionary<string, MacSimulatorHidSession> simulatorHidSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private long nextPointerId = 10_000;
    private bool disposed;

    public HeadlessHostDeviceDriver(DeviceService devices, RuntimeOptions options)
    {
        this.devices = devices ?? throw new ArgumentNullException(nameof(devices));
        ArgumentNullException.ThrowIfNull(options);
        configuredAdbPath = options.AdbPath;
        iosPhysicalDeviceInput = new IosPhysicalDeviceAppiumClient(
            IosPhysicalDeviceAppiumOptions.Resolve(options));
        iosSimulatorAccessibility = new IosSimulatorAccessibilityClient(options.IosSimulatorAxePath);
        var webDriverAgentOptions = IosWebDriverAgentOptions.Resolve(options);
        iosWebDriverAgentAccessibility = webDriverAgentOptions.IsConfigured
            ? new IosWebDriverAgentClient(webDriverAgentOptions)
            : null;
    }

    public async Task<IReadOnlyList<DeviceLifecycleDevice>> ListDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<DeviceLifecycleDevice>(inventory.Devices.Count);
        foreach (var device in inventory.Devices)
        {
            IReadOnlyList<DeviceLifecycleApplication> applications = [];
            if (device.IsBooted)
            {
                try
                {
                    applications = (await devices.ListApplicationsAsync(
                            device.Platform,
                            device.Identifier,
                            cancellationToken)
                        .ConfigureAwait(false))
                        .Select(static app => new DeviceLifecycleApplication(
                            app.Identifier,
                            app.Name))
                        .ToArray();
                }
                catch (Exception exception) when (IsDeviceFailure(exception))
                {
                    applications = [];
                }
            }

            results.Add(new DeviceLifecycleDevice(
                device.Identifier,
                device.Name,
                device.Platform,
                device.Runtime,
                device.State,
                device.IsBooted,
                applications,
                device.Kind));
        }

        return results;
    }

    public async Task<DeviceLifecycleResult> StartDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceLifecycleResult.Failure(
                "startDevice",
                $"Device '{deviceIdentifier}' was not found.",
                deviceIdentifier);
        }

        var result = await devices.StartAsync(
            device.Platform,
            device.Identifier,
            cancellationToken).ConfigureAwait(false);
        return ToLifecycleResult(result, "startDevice");
    }

    public async Task<DeviceLifecycleResult> LaunchApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceLifecycleResult.Failure(
                "launchApplication",
                $"Device '{deviceIdentifier}' was not found.",
                deviceIdentifier,
                bundleIdentifier: bundleIdentifier);
        }

        var result = await devices.LaunchApplicationAsync(
            device.Platform,
            device.Identifier,
            bundleIdentifier,
            cancellationToken).ConfigureAwait(false);
        return ToLifecycleResult(result, "launchApplication", bundleIdentifier);
    }

    public async Task<DeviceLifecycleResult> BackgroundApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceLifecycleResult.Failure(
                "backgroundApplication",
                $"Device '{deviceIdentifier}' was not found.",
                deviceIdentifier,
                bundleIdentifier: bundleIdentifier);
        }

        if (!device.IsVirtual && device.Platform != DevicePlatforms.Android)
        {
            return DeviceLifecycleResult.Failure(
                "backgroundApplication",
                "Moving an app to the background is supported on Android devices and iOS Simulators.",
                device.Identifier,
                device.Platform,
                bundleIdentifier);
        }

        var result = await PressButtonAsync(
            new UiButtonRequest(
                string.Empty,
                device.Identifier,
                "home",
                bundleIdentifier),
            cancellationToken).ConfigureAwait(false);
        return new DeviceLifecycleResult(
            result.IsSuccess,
            device.Identifier,
            device.Platform,
            "backgroundApplication",
            bundleIdentifier,
            result.IsSuccess
                ? "The Home button was pressed to move the application to the background."
                : result.Message);
    }

    public async Task<DeviceLifecycleResult> TerminateApplicationAsync(
        string deviceIdentifier,
        string bundleIdentifier,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return DeviceLifecycleResult.Failure(
                "terminateApplication",
                $"Device '{deviceIdentifier}' was not found.",
                deviceIdentifier,
                bundleIdentifier: bundleIdentifier);
        }

        var result = await devices.TerminateApplicationAsync(
            device.Platform,
            device.Identifier,
            bundleIdentifier,
            cancellationToken).ConfigureAwait(false);
        return ToLifecycleResult(result, "terminateApplication", bundleIdentifier);
    }

    public UiInputAvailability GetAvailability(string deviceIdentifier)
    {
        if (disposed)
        {
            return UiInputAvailability.Unavailable("The headless device input driver has stopped.");
        }

        if (string.IsNullOrWhiteSpace(deviceIdentifier))
        {
            return UiInputAvailability.Unavailable("A simulator or emulator identifier is required.");
        }

        if (devices.TryGetKnownDevice(deviceIdentifier.Trim(), out var platform, out var kind))
        {
            if (platform == DevicePlatforms.Android)
            {
                return new UiInputAvailability(true, AndroidBackend, "ADB device input is available.");
            }

            if (DeviceKinds.IsPhysical(kind))
            {
                return new UiInputAvailability(
                    true,
                    IosPhysicalBackend,
                    $"Physical iOS input will use Appium at {iosPhysicalDeviceInput.Endpoint}.");
            }
        }

        var simulatorHidCapability = devices.GetIosSimulatorHidCapability();
        return new UiInputAvailability(
            simulatorHidCapability.IsAvailable,
            simulatorHidCapability.IsAvailable ? IosBackend : string.Empty,
            simulatorHidCapability.Message);
    }

    internal string? GetAppiumSessionId(
        string deviceIdentifier,
        string applicationIdentifier)
        => iosPhysicalDeviceInput.GetSessionId(deviceIdentifier, applicationIdentifier);

    internal Task PreparePhysicalIosMonitoringAsync(string deviceIdentifier,
        string applicationIdentifier, CancellationToken cancellationToken)
        => iosPhysicalDeviceInput.PrepareMonitoringAsync(deviceIdentifier, applicationIdentifier, cancellationToken);

    internal Task<bool> IsPhysicalIosApplicationForegroundAsync(string deviceIdentifier,
        string applicationIdentifier, CancellationToken cancellationToken)
        => iosPhysicalDeviceInput.IsApplicationForegroundAsync(deviceIdentifier, applicationIdentifier, cancellationToken);

    internal Task<byte[]> CapturePhysicalIosScreenshotAsync(string deviceIdentifier,
        string applicationIdentifier, CancellationToken cancellationToken)
        => iosPhysicalDeviceInput.GetScreenshotAsync(deviceIdentifier, applicationIdentifier, cancellationToken);

    internal Task ReleasePhysicalIosMonitoringAsync(string deviceIdentifier,
        string applicationIdentifier, CancellationToken cancellationToken)
        => iosPhysicalDeviceInput.ReleaseSessionAsync(deviceIdentifier, applicationIdentifier, cancellationToken);

    public async Task<string> GetDevicePlatformAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputGate = GetInputGate(deviceIdentifier);
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
            if (device is null || !device.IsBooted)
            {
                throw new InvalidOperationException(
                    $"The target runtime device '{deviceIdentifier}' is not booted or available.");
            }

            return device.Platform;
        }
        finally
        {
            inputGate.Release();
        }
    }

    public async Task<UiViewport> GetViewportAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputGate = GetInputGate(deviceIdentifier);
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
            if (device is null || !device.IsBooted)
            {
                throw new InvalidOperationException(
                    $"The target runtime device '{deviceIdentifier}' is not booted or available.");
            }

            if (device.Platform == DevicePlatforms.Android)
            {
                var size = await GetAndroidDisplaySizeAsync(device.Identifier, cancellationToken)
                    .ConfigureAwait(false);
                return new UiViewport(
                    device.Platform,
                    size.Width,
                    size.Height,
                    "pixels");
            }

            if (device.IsPhysical)
            {
                throw new PlatformNotSupportedException(
                    "Absolute-coordinate CLI input is currently available for iOS Simulators, not physical iOS devices.");
            }

            var simulatorHidCapability = devices.GetIosSimulatorHidCapability();
            if (!simulatorHidCapability.IsAvailable)
            {
                throw new MacSimulatorHidException(simulatorHidCapability.Message);
            }

            var session = await GetSimulatorHidSessionAsync(device.Identifier, cancellationToken)
                .ConfigureAwait(false);
            var metrics = session.GetMainScreenMetrics(device.Identifier);
            var screenshotSize = await devices.GetIosSimulatorScreenshotSizeAsync(
                    device.Identifier,
                    cancellationToken)
                .ConfigureAwait(false);
            return CreateIosSimulatorViewport(metrics, screenshotSize);
        }
        finally
        {
            inputGate.Release();
        }
    }

    public async Task<UiAccessibilityResult> CaptureAccessibilityAsync(
        UiAccessibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputGate = GetInputGate(request.DeviceIdentifier);
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = await ResolveDeviceAsync(request.DeviceIdentifier, cancellationToken)
                .ConfigureAwait(false);
            if (device is null || !device.IsBooted)
            {
                return UiAccessibilityResult.Failure(
                    $"The target runtime device '{request.DeviceIdentifier}' is not booted or available.");
            }

            if (device.Platform == DevicePlatforms.Android)
            {
                var size = await GetAndroidDisplaySizeAsync(device.Identifier, cancellationToken)
                    .ConfigureAwait(false);
                var source = await CaptureAndroidAccessibilitySourceAsync(
                    device.Identifier,
                    cancellationToken).ConfigureAwait(false);
                var payload = DeviceAccessibilityTreeNormalizer.NormalizeAndroid(
                    source,
                    size.Width,
                    size.Height,
                    request.MaxNodes,
                    request.MaxDepth);
                var keyboardVisible = await CreateAdbClient()
                    .GetSoftwareKeyboardVisibilityAsync(device.Identifier, cancellationToken)
                    .ConfigureAwait(false);
                if (keyboardVisible.HasValue)
                {
                    payload["keyboardVisible"] = keyboardVisible.Value;
                    payload["keyboardVisibilitySource"] = "android-input-method-service";
                }
                else
                {
                    // UIAutomator omits third-party IME windows on physical devices.
                    // An unknown input-method state must not be reported as a reliable false.
                    payload.Remove("keyboardVisible");
                }

                return new UiAccessibilityResult(
                    true,
                    AndroidAccessibilityBackend,
                    "Captured the Android device accessibility hierarchy through UIAutomator and input-method state through Android's system service.",
                    payload);
            }

            if (!OperatingSystem.IsMacOS())
            {
                return UiAccessibilityResult.Failure(
                    "iOS device accessibility capture is available only on macOS.",
                    IosSimulatorAccessibilityBackend);
            }

            if (!device.IsPhysical)
            {
                string? nativeAxServiceFailure = null;
                try
                {
                    var session = await GetSimulatorHidSessionAsync(device.Identifier, cancellationToken)
                        .ConfigureAwait(false);
                    var sourceJson = session.CaptureAccessibilityTreeJson(device.Identifier);
                    var source = JsonNode.Parse(sourceJson)
                                 ?? throw new InvalidDataException(
                                     "The resident CoreSimulator accessibility service returned invalid JSON.");
                    var payload = DeviceAccessibilityTreeNormalizer.NormalizeIosSimulator(
                        source,
                        request.MaxNodes,
                        request.MaxDepth);
                    return new UiAccessibilityResult(
                        true,
                        IosSimulatorAccessibilityBackend,
                        "Captured the iOS Simulator semantic hierarchy through Ansight's resident CoreSimulator accessibility bridge.",
                        payload);
                }
                catch (Exception exception) when (IsDeviceFailure(exception))
                {
                    nativeAxServiceFailure = exception.Message;
                }

                string? axeFailure = null;
                if (iosSimulatorAccessibility.IsAvailable)
                {
                    try
                    {
                        var source = await iosSimulatorAccessibility.DescribeUiAsync(
                            device.Identifier,
                            cancellationToken).ConfigureAwait(false);
                        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIosSimulator(
                            source,
                            request.MaxNodes,
                            request.MaxDepth);
                        return new UiAccessibilityResult(
                            true,
                            IosSimulatorAccessibilityBackend,
                            "Captured the iOS Simulator semantic hierarchy through the CoreSimulator accessibility service.",
                            payload);
                    }
                    catch (Exception exception) when (IsDeviceFailure(exception))
                    {
                        axeFailure = exception.Message;
                    }
                }
                else
                {
                    axeFailure = iosSimulatorAccessibility.AvailabilityMessage;
                }

                if (iosWebDriverAgentAccessibility is not null)
                {
                    try
                    {
                        return await CaptureDirectWdaAccessibilityAsync(
                            request,
                            iosWebDriverAgentAccessibility,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsDeviceFailure(exception))
                    {
                        return UiAccessibilityResult.Failure(
                            $"The resident CoreSimulator accessibility bridge was unavailable: {nativeAxServiceFailure} "
                            + $"The AXe fallback was unavailable: {axeFailure} "
                            + $"The configured direct WebDriverAgent fallback also failed: {exception.Message}",
                            IosWdaAccessibilityBackend);
                    }
                }

                return UiAccessibilityResult.Failure(
                    $"The resident CoreSimulator accessibility bridge was unavailable: {nativeAxServiceFailure} "
                    + $"The AXe fallback was unavailable: {axeFailure}",
                    IosSimulatorAccessibilityBackend);
            }

            if (iosWebDriverAgentAccessibility is not null)
            {
                try
                {
                    return await CaptureDirectWdaAccessibilityAsync(
                        request,
                        iosWebDriverAgentAccessibility,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsDeviceFailure(exception))
                {
                    // Preserve the existing Appium/XCUITest physical-device path as a final fallback.
                }
            }

            var pageSource = await iosPhysicalDeviceInput.GetPageSourceAsync(
                device.Identifier,
                RequireApplicationIdentifier(request.ApplicationIdentifier),
                cancellationToken).ConfigureAwait(false);
            var iosPayload = DeviceAccessibilityTreeNormalizer.NormalizeIos(
                pageSource.Source,
                pageSource.ViewportWidth,
                pageSource.ViewportHeight,
                request.MaxNodes,
                request.MaxDepth);
            return new UiAccessibilityResult(
                true,
                IosAppiumAccessibilityBackend,
                "Captured the physical iOS accessibility hierarchy through Appium/XCUITest and WebDriverAgent.",
                iosPayload);
        }
        catch (Exception exception) when (IsDeviceFailure(exception))
        {
            return UiAccessibilityResult.Failure(exception.Message);
        }
        finally
        {
            inputGate.Release();
        }
    }

    public Task<UiInputResult> PrepareForInputAsync(
        UiInputPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteInputAsync(
            request.DeviceIdentifier,
            static (device, _) => Task.FromResult(Success(
                device.Platform == DevicePlatforms.Android ? AndroidBackend : IosBackend,
                "The device input surface is ready.")),
            cancellationToken);
    }

    public Task<UiInputResult> TapAsync(
        UiTapRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteInputAsync(
            request.DeviceIdentifier,
            async (device, token) =>
            {
                if (device.Platform == DevicePlatforms.Android)
                {
                    var size = await GetAndroidDisplaySizeAsync(device.Identifier, token).ConfigureAwait(false);
                    var result = await devices.SendTapAsync(
                        device.Platform,
                        device.Identifier,
                        ToDisplayCoordinate(request.NormalizedX, size.Width),
                        ToDisplayCoordinate(request.NormalizedY, size.Height),
                        token).ConfigureAwait(false);
                    return ToInputResult(result, AndroidBackend);
                }

                if (device.IsPhysical)
                {
                    await iosPhysicalDeviceInput.TapAsync(
                        device.Identifier,
                        RequireApplicationIdentifier(request.ApplicationIdentifier),
                        request.NormalizedX,
                        request.NormalizedY,
                        token).ConfigureAwait(false);
                    return Success(IosPhysicalBackend, "Physical iOS tap delivered through Appium/XCUITest.");
                }

                var session = await GetSimulatorHidSessionAsync(device.Identifier, token)
                    .ConfigureAwait(false);
                var pointerId = Interlocked.Increment(ref nextPointerId);
                var timestamp = Environment.TickCount64;
                session.SendPointer(
                    device.Identifier,
                    MacSimulatorPointerPhase.Down,
                    request.NormalizedX,
                    request.NormalizedY,
                    pointerId,
                    timestamp);
                await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                session.SendPointer(
                    device.Identifier,
                    MacSimulatorPointerPhase.Up,
                    request.NormalizedX,
                    request.NormalizedY,
                    pointerId,
                    timestamp + 50);
                return Success(IosBackend, "iOS Simulator tap delivered.");
            },
            cancellationToken);

    public Task<UiInputResult> SwipeAsync(
        UiSwipeRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteInputAsync(
            request.DeviceIdentifier,
            async (device, token) =>
            {
                var duration = Math.Clamp(request.DurationMilliseconds, 50, 2_000);
                if (device.Platform == DevicePlatforms.Android)
                {
                    var size = await GetAndroidDisplaySizeAsync(device.Identifier, token).ConfigureAwait(false);
                    var result = await devices.SendSwipeAsync(
                        device.Platform,
                        device.Identifier,
                        ToDisplayCoordinate(request.StartNormalizedX, size.Width),
                        ToDisplayCoordinate(request.StartNormalizedY, size.Height),
                        ToDisplayCoordinate(request.EndNormalizedX, size.Width),
                        ToDisplayCoordinate(request.EndNormalizedY, size.Height),
                        duration,
                        token).ConfigureAwait(false);
                    return ToInputResult(result, AndroidBackend);
                }

                if (device.IsPhysical)
                {
                    await iosPhysicalDeviceInput.SwipeAsync(
                        device.Identifier,
                        RequireApplicationIdentifier(request.ApplicationIdentifier),
                        request.StartNormalizedX,
                        request.StartNormalizedY,
                        request.EndNormalizedX,
                        request.EndNormalizedY,
                        duration,
                        token).ConfigureAwait(false);
                    return Success(IosPhysicalBackend, "Physical iOS swipe delivered through Appium/XCUITest.");
                }

                await SendSimulatorSwipeAsync(device.Identifier, request, duration, token).ConfigureAwait(false);
                return Success(IosBackend, "iOS Simulator swipe delivered.");
            },
            cancellationToken);

    public Task<UiInputResult> PinchAsync(
        UiPinchRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteInputAsync(
            request.DeviceIdentifier,
            async (device, token) =>
            {
                if (device.Platform == DevicePlatforms.Android)
                {
                    return new UiInputResult(
                        false,
                        AndroidBackend,
                        "Android ADB input does not support multi-touch pinch gestures. Pinch requires a device input driver with multi-touch support.");
                }

                if (device.IsPhysical)
                {
                    return new UiInputResult(
                        false,
                        IosPhysicalBackend,
                        "Physical iOS pinch input is not available through the headless driver.");
                }

                var duration = Math.Clamp(request.DurationMilliseconds, 50, 2_000);
                await SendSimulatorPinchAsync(
                    device.Identifier,
                    request,
                    duration,
                    token).ConfigureAwait(false);
                return Success(IosBackend, "iOS Simulator pinch delivered.");
            },
            cancellationToken);

    public Task<UiInputResult> TypeTextAsync(
        UiTextRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteInputAsync(
            request.DeviceIdentifier,
            async (device, token) =>
            {
                if (device.Platform == DevicePlatforms.Android)
                {
                    if (request.ReplaceExisting)
                    {
                        await ClearAndroidTextAsync(device.Identifier, token).ConfigureAwait(false);
                    }

                    if (request.Text.Length == 0)
                    {
                        return Success(
                            AndroidBackend,
                            request.ReplaceExisting
                                ? "Android text field cleared."
                                : "Android text input completed without appending characters.");
                    }

                    var result = await devices.SendTextAsync(
                        device.Platform,
                        device.Identifier,
                        request.Text,
                        token).ConfigureAwait(false);
                    return ToInputResult(result, AndroidBackend);
                }

                if (device.IsPhysical)
                {
                    await iosPhysicalDeviceInput.TypeTextAsync(
                        device.Identifier,
                        RequireApplicationIdentifier(request.ApplicationIdentifier),
                        request.Text,
                        request.ReplaceExisting,
                        token).ConfigureAwait(false);
                    return Success(IosPhysicalBackend, "Physical iOS text input delivered through Appium/XCUITest.");
                }

                var session = await GetSimulatorHidSessionAsync(device.Identifier, token)
                    .ConfigureAwait(false);
                if (request.ReplaceExisting)
                {
                    ClearSimulatorText(session, device.Identifier);
                }

                foreach (var stroke in SimulatorTextInputMapper.Map(request.Text))
                {
                    token.ThrowIfCancellationRequested();
                    if (stroke.RequiresShift)
                    {
                        session.SendKey(device.Identifier, 225, MacSimulatorKeyPhase.Down);
                    }

                    session.SendKey(device.Identifier, stroke.UsageCode, MacSimulatorKeyPhase.Down);
                    session.SendKey(device.Identifier, stroke.UsageCode, MacSimulatorKeyPhase.Up);
                    if (stroke.RequiresShift)
                    {
                        session.SendKey(device.Identifier, 225, MacSimulatorKeyPhase.Up);
                    }
                }

                return Success(IosBackend, "iOS Simulator text input delivered.");
            },
            cancellationToken);

    public Task<UiInputResult> PressButtonAsync(
        UiButtonRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteInputAsync(
            request.DeviceIdentifier,
            async (device, token) =>
            {
                if (device.Platform == DevicePlatforms.Android)
                {
                    var result = await devices.SendButtonAsync(
                        device.Platform,
                        device.Identifier,
                        request.Button,
                        token).ConfigureAwait(false);
                    return ToInputResult(result, AndroidBackend);
                }

                if (device.IsPhysical)
                {
                    await iosPhysicalDeviceInput.PressButtonAsync(
                        device.Identifier,
                        RequireApplicationIdentifier(request.ApplicationIdentifier),
                        request.Button,
                        token).ConfigureAwait(false);
                    return Success(IosPhysicalBackend, "Physical iOS button input delivered through Appium/XCUITest.");
                }

                var session = await GetSimulatorHidSessionAsync(device.Identifier, token)
                    .ConfigureAwait(false);
                if (string.Equals(request.Button, "back", StringComparison.OrdinalIgnoreCase))
                {
                    session.SendKey(device.Identifier, 41, MacSimulatorKeyPhase.Down);
                    session.SendKey(device.Identifier, 41, MacSimulatorKeyPhase.Up);
                }
                else
                {
                    var button = request.Button.ToLowerInvariant() switch
                    {
                        "home" => MacSimulatorButton.Home,
                        "lock" or "power" => MacSimulatorButton.Lock,
                        "volume-up" => MacSimulatorButton.VolumeUp,
                        "volume-down" => MacSimulatorButton.VolumeDown,
                        _ => throw new ArgumentException(
                            $"Unsupported iOS Simulator button '{request.Button}'.",
                            nameof(request))
                    };
                    session.SendButton(device.Identifier, button, MacSimulatorButtonPhase.Down);
                    session.SendButton(device.Identifier, button, MacSimulatorButtonPhase.Up);
                }

                return Success(IosBackend, "iOS Simulator button input delivered.");
            },
            cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lock (simulatorHidSessionsGate)
        {
            foreach (var session in simulatorHidSessions.Values)
            {
                session.Dispose();
            }

            simulatorHidSessions.Clear();
        }

        iosWebDriverAgentAccessibility?.Dispose();
        iosPhysicalDeviceInput.Dispose();
        foreach (var inputGate in inputGates.Values)
        {
            inputGate.Dispose();
        }
    }

    private async Task<UiInputResult> ExecuteInputAsync(
        string deviceIdentifier,
        Func<DeviceDescriptor, CancellationToken, Task<UiInputResult>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var inputGate = GetInputGate(deviceIdentifier);
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = await ResolveDeviceAsync(deviceIdentifier, cancellationToken).ConfigureAwait(false);
            if (device is null || !device.IsBooted)
            {
                return UiInputResult.Failure(
                    $"The target runtime device '{deviceIdentifier}' is not booted or available.");
            }

            if (device.Platform == DevicePlatforms.Ios && device.IsPhysical)
            {
                if (!OperatingSystem.IsMacOS())
                {
                    return UiInputResult.Failure("Physical iOS input is available only on macOS.");
                }
            }
            else if (device.Platform == DevicePlatforms.Ios)
            {
                var simulatorHidCapability = devices.GetIosSimulatorHidCapability();
                if (!simulatorHidCapability.IsAvailable)
                {
                    return UiInputResult.Failure(simulatorHidCapability.Message);
                }
            }

            if (device.Platform == DevicePlatforms.Android)
            {
                await CollapseAndroidSystemUiPanelsAsync(device.Identifier, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await action(device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSimulatorHidFailure(exception))
        {
            return new UiInputResult(false, IosBackend, exception.Message);
        }
        catch (Exception exception) when (IsDeviceFailure(exception))
        {
            return UiInputResult.Failure(exception.Message);
        }
        finally
        {
            inputGate.Release();
        }
    }

    private async Task SendSimulatorSwipeAsync(
        string deviceIdentifier,
        UiSwipeRequest request,
        int duration,
        CancellationToken cancellationToken)
    {
        var session = await GetSimulatorHidSessionAsync(deviceIdentifier, cancellationToken)
            .ConfigureAwait(false);
        var pointerId = Interlocked.Increment(ref nextPointerId);
        var timestamp = Environment.TickCount64;
        session.SendPointer(
            deviceIdentifier,
            MacSimulatorPointerPhase.Down,
            request.StartNormalizedX,
            request.StartNormalizedY,
            pointerId,
            timestamp);
        var stopwatch = Stopwatch.StartNew();
        var moveCount = Math.Clamp((int)Math.Floor(duration / 40d) - 1, 1, 49);
        for (var index = 1; index <= moveCount; index++)
        {
            var fraction = index / (double)(moveCount + 1);
            var targetElapsedMilliseconds = (int)Math.Round(duration * fraction);
            var remainingMilliseconds = targetElapsedMilliseconds - stopwatch.ElapsedMilliseconds;
            if (remainingMilliseconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }

            session.SendPointer(
                deviceIdentifier,
                MacSimulatorPointerPhase.Move,
                request.StartNormalizedX + ((request.EndNormalizedX - request.StartNormalizedX) * fraction),
                request.StartNormalizedY + ((request.EndNormalizedY - request.StartNormalizedY) * fraction),
                pointerId,
                timestamp + targetElapsedMilliseconds);
        }

        var finalRemainingMilliseconds = duration - stopwatch.ElapsedMilliseconds;
        if (finalRemainingMilliseconds > 0)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(finalRemainingMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }

        session.SendPointer(
            deviceIdentifier,
            MacSimulatorPointerPhase.Up,
            request.EndNormalizedX,
            request.EndNormalizedY,
            pointerId,
            timestamp + duration);
    }

    private static async Task<UiAccessibilityResult> CaptureDirectWdaAccessibilityAsync(
        UiAccessibilityRequest request,
        IosWebDriverAgentClient client,
        CancellationToken cancellationToken)
    {
        var pageSource = await client.GetPageSourceAsync(
            RequireApplicationIdentifier(request.ApplicationIdentifier),
            cancellationToken).ConfigureAwait(false);
        var payload = DeviceAccessibilityTreeNormalizer.NormalizeIos(
            pageSource.Source,
            pageSource.ViewportWidth,
            pageSource.ViewportHeight,
            request.MaxNodes,
            request.MaxDepth);
        return new UiAccessibilityResult(
            true,
            IosWdaAccessibilityBackend,
            "Captured the iOS accessibility hierarchy directly through WebDriverAgent without Appium.",
            payload);
    }

    private async Task SendSimulatorPinchAsync(
        string deviceIdentifier,
        UiPinchRequest request,
        int duration,
        CancellationToken cancellationToken)
    {
        var session = await GetSimulatorHidSessionAsync(deviceIdentifier, cancellationToken)
            .ConfigureAwait(false);
        var pointerId = Interlocked.Increment(ref nextPointerId);
        var timestamp = Environment.TickCount64;
        SendSimulatorPinchPointer(
            session,
            deviceIdentifier,
            request,
            MacSimulatorPointerPhase.Down,
            pointerId,
            timestamp,
            0);
        var stopwatch = Stopwatch.StartNew();
        var moveCount = Math.Clamp((int)Math.Floor(duration / 40d) - 1, 1, 49);
        for (var index = 1; index <= moveCount; index++)
        {
            var fraction = index / (double)(moveCount + 1);
            var targetElapsedMilliseconds = (int)Math.Round(duration * fraction);
            var remainingMilliseconds = targetElapsedMilliseconds - stopwatch.ElapsedMilliseconds;
            if (remainingMilliseconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }

            SendSimulatorPinchPointer(
                session,
                deviceIdentifier,
                request,
                MacSimulatorPointerPhase.Move,
                pointerId,
                timestamp + targetElapsedMilliseconds,
                fraction);
        }

        var finalRemainingMilliseconds = duration - stopwatch.ElapsedMilliseconds;
        if (finalRemainingMilliseconds > 0)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(finalRemainingMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }

        SendSimulatorPinchPointer(
            session,
            deviceIdentifier,
            request,
            MacSimulatorPointerPhase.Up,
            pointerId,
            timestamp + duration,
            1);
    }

    private static void SendSimulatorPinchPointer(
        MacSimulatorHidSession session,
        string deviceIdentifier,
        UiPinchRequest request,
        MacSimulatorPointerPhase phase,
        long pointerId,
        long timestampMilliseconds,
        double fraction)
    {
        var primaryX = Interpolate(
            request.PrimaryStartNormalizedX,
            request.PrimaryEndNormalizedX,
            fraction);
        var primaryY = Interpolate(
            request.PrimaryStartNormalizedY,
            request.PrimaryEndNormalizedY,
            fraction);
        var secondaryX = Interpolate(
            request.SecondaryStartNormalizedX,
            request.SecondaryEndNormalizedX,
            fraction);
        var secondaryY = Interpolate(
            request.SecondaryStartNormalizedY,
            request.SecondaryEndNormalizedY,
            fraction);
        session.SendPointer(
            deviceIdentifier,
            phase,
            primaryX,
            primaryY,
            new MacSimulatorTouchContact(secondaryX, secondaryY),
            pointerId,
            timestampMilliseconds);
    }

    private static double Interpolate(double start, double end, double fraction)
        => start + ((end - start) * fraction);

    private SemaphoreSlim GetInputGate(string deviceIdentifier)
    {
        var normalizedIdentifier = string.IsNullOrWhiteSpace(deviceIdentifier)
            ? string.Empty
            : deviceIdentifier.Trim();
        return inputGates.GetOrAdd(normalizedIdentifier, static _ => new SemaphoreSlim(1, 1));
    }

    private Task<MacSimulatorHidSession> GetSimulatorHidSessionAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedIdentifier = deviceIdentifier.Trim();
        lock (simulatorHidSessionsGate)
        {
            if (simulatorHidSessions.TryGetValue(normalizedIdentifier, out var existingSession))
            {
                return Task.FromResult(existingSession);
            }

            var capability = devices.GetIosSimulatorHidCapability();
            if (!capability.IsAvailable || string.IsNullOrWhiteSpace(capability.Backend))
            {
                throw new MacSimulatorHidException(capability.Message);
            }

            var session = new MacSimulatorHidSession(capability.Backend);
            simulatorHidSessions.Add(normalizedIdentifier, session);
            return Task.FromResult(session);
        }
    }

    private async Task<DeviceDescriptor?> ResolveDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var normalizedIdentifier = string.IsNullOrWhiteSpace(deviceIdentifier)
            ? string.Empty
            : deviceIdentifier.Trim();
        if (normalizedIdentifier.Length == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        lock (deviceResolutionGate)
        {
            if (deviceResolutionCache.TryGetValue(normalizedIdentifier, out var cached)
                && now - cached.ResolvedAtUtc <= DeviceResolutionCacheDuration)
            {
                return cached.Device;
            }
        }

        var inventory = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var resolved = inventory.Devices.FirstOrDefault(device => string.Equals(
                           device.Identifier,
                           normalizedIdentifier,
                           StringComparison.OrdinalIgnoreCase))
                       ?? inventory.Devices.FirstOrDefault(device =>
                device.IsBooted &&
                device.Platform == DevicePlatforms.Android &&
                device.IsVirtual &&
                string.Equals(device.Name, normalizedIdentifier, StringComparison.OrdinalIgnoreCase));
        lock (deviceResolutionGate)
        {
            if (resolved?.IsBooted == true)
            {
                deviceResolutionCache[normalizedIdentifier] = new DeviceResolutionCacheEntry(
                    DateTimeOffset.UtcNow,
                    resolved);
            }
            else
            {
                deviceResolutionCache.Remove(normalizedIdentifier);
            }
        }
        return resolved;
    }

    private async Task<HeadlessAndroidDisplaySize> GetAndroidDisplaySizeAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var result = await CreateAdbClient().RunAsync(
            ["-s", deviceIdentifier, "shell", "wm", "size"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                FirstNonEmpty(result.StandardError, result.StandardOutput, "Could not read Android display size."));
        }

        var matches = AndroidDisplaySizePattern().Matches(result.StandardOutput);
        if (matches.Count == 0
            || !int.TryParse(matches[^1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(matches[^1].Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            throw new InvalidOperationException("ADB returned no usable Android display size.");
        }

        return new HeadlessAndroidDisplaySize(width, height);
    }

    private async Task<string> CaptureAndroidAccessibilitySourceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var adb = CreateAdbClient();
        var dumpResult = await adb.RunAsync(
            [
                "-s",
                deviceIdentifier,
                "shell",
                "uiautomator",
                "dump",
                "--compressed",
                AndroidAccessibilityDumpPath
            ],
            cancellationToken).ConfigureAwait(false);
        if (!dumpResult.IsSuccess)
        {
            throw new InvalidOperationException(
                FirstNonEmpty(
                    dumpResult.StandardError,
                    dumpResult.StandardOutput,
                    "UIAutomator could not capture the Android accessibility hierarchy."));
        }

        var sourceResult = await adb.RunAsync(
            ["-s", deviceIdentifier, "exec-out", "cat", AndroidAccessibilityDumpPath],
            cancellationToken).ConfigureAwait(false);
        if (!sourceResult.IsSuccess || string.IsNullOrWhiteSpace(sourceResult.StandardOutput))
        {
            throw new InvalidOperationException(
                FirstNonEmpty(
                    sourceResult.StandardError,
                    sourceResult.StandardOutput,
                    "ADB returned no Android accessibility hierarchy."));
        }

        return sourceResult.StandardOutput;
    }

    private async Task ClearAndroidTextAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var client = CreateAdbClient();
        var selection = await client.RunAsync(
            ["-s", deviceIdentifier, "shell", "input", "keycombination", "113", "29"],
            cancellationToken).ConfigureAwait(false);
        if (!selection.IsSuccess)
        {
            throw new InvalidOperationException(
                FirstNonEmpty(selection.StandardError, selection.StandardOutput, "Could not select Android text."));
        }

        var deletion = await client.RunAsync(
            ["-s", deviceIdentifier, "shell", "input", "keyevent", "67"],
            cancellationToken).ConfigureAwait(false);
        if (!deletion.IsSuccess)
        {
            throw new InvalidOperationException(
                FirstNonEmpty(deletion.StandardError, deletion.StandardOutput, "Could not clear Android text."));
        }
    }

    private async Task CollapseAndroidSystemUiPanelsAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var result = await CreateAdbClient().CollapseSystemUiPanelsAsync(
            deviceIdentifier,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                FirstNonEmpty(
                    result.StandardError,
                    result.StandardOutput,
                    "Could not dismiss transient Android system UI before input."));
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

    private static void ClearSimulatorText(MacSimulatorHidSession session, string deviceIdentifier)
    {
        const uint commandUsageCode = 227;
        const uint aUsageCode = 4;
        const uint backspaceUsageCode = 42;
        session.SendKey(deviceIdentifier, commandUsageCode, MacSimulatorKeyPhase.Down);
        session.SendKey(deviceIdentifier, aUsageCode, MacSimulatorKeyPhase.Down);
        session.SendKey(deviceIdentifier, aUsageCode, MacSimulatorKeyPhase.Up);
        session.SendKey(deviceIdentifier, commandUsageCode, MacSimulatorKeyPhase.Up);
        session.SendKey(deviceIdentifier, backspaceUsageCode, MacSimulatorKeyPhase.Down);
        session.SendKey(deviceIdentifier, backspaceUsageCode, MacSimulatorKeyPhase.Up);
    }

    private static DeviceLifecycleResult ToLifecycleResult(
        DeviceOperationResult result,
        string operation,
        string? bundleIdentifier = null)
        => new(
            result.IsSuccess,
            result.DeviceIdentifier,
            result.Platform,
            operation,
            bundleIdentifier,
            result.Message);

    private static UiInputResult ToInputResult(
        DeviceOperationResult result,
        string backend)
        => new(result.IsSuccess, backend, result.Message);

    private static UiInputResult Success(string backend, string message)
        => new(true, backend, message);

    private static int ToDisplayCoordinate(double normalizedCoordinate, int displaySize)
        => (int)Math.Round(Math.Clamp(normalizedCoordinate, 0, 1) * Math.Max(0, displaySize - 1));

    internal static UiViewport CreateIosSimulatorViewport(
        MacSimulatorDisplayMetrics metrics,
        HeadlessIosScreenshotSize screenshotSize)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (!double.IsFinite(metrics.Scale) || metrics.Scale <= 0)
        {
            throw new InvalidOperationException("The iOS Simulator display scale is invalid.");
        }
        if (screenshotSize.Width <= 0 || screenshotSize.Height <= 0)
        {
            throw new InvalidOperationException("The iOS Simulator screenshot dimensions are invalid.");
        }

        return new UiViewport(
            DevicePlatforms.Ios,
            screenshotSize.Width / metrics.Scale,
            screenshotSize.Height / metrics.Scale,
            "logical-points");
    }

    private static bool IsDeviceFailure(Exception exception)
        => exception is ArgumentException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or HttpRequestException
            or MacSimulatorHidException
            or PlatformNotSupportedException
            or TimeoutException;

    internal static bool IsSimulatorHidFailure(Exception exception)
        => exception is MacSimulatorHidException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private static string RequireApplicationIdentifier(string? applicationIdentifier)
    {
        if (string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            throw new InvalidOperationException(
                "Physical iOS input requires the target app bundle identifier.");
        }

        return applicationIdentifier.Trim();
    }

    private sealed record DeviceResolutionCacheEntry(
        DateTimeOffset ResolvedAtUtc,
        DeviceDescriptor Device);

    [GeneratedRegex(@"(\d+)x(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AndroidDisplaySizePattern();

}
