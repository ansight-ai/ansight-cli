using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal sealed class DeviceInputCommandRuntime : IDeviceInputCommandRuntime, IDisposable
{
    private const string InputSessionId = "cli-device-input";
    private readonly DeviceService devices;
    private readonly HeadlessHostDeviceDriver driver;
    private bool disposed;

    public DeviceInputCommandRuntime(RuntimeOptions options)
    {
        devices = new DeviceService(options);
        driver = new HeadlessHostDeviceDriver(devices, options);
    }

    public Task<DeviceOperationResult> SendTapAsync(
        string platform,
        string deviceIdentifier,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            return devices.SendTapAsync(
                normalizedPlatform,
                deviceIdentifier,
                x,
                y,
                cancellationToken);
        }

        return ExecuteIosInputAsync(
            "tap",
            deviceIdentifier,
            async () =>
            {
                var viewport = await RequireIosViewportAsync(deviceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return await driver.TapAsync(
                        CreateTapRequest(deviceIdentifier, viewport, x, y),
                        cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    public Task<DeviceOperationResult> SendSwipeAsync(
        string platform,
        string deviceIdentifier,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            return devices.SendSwipeAsync(
                normalizedPlatform,
                deviceIdentifier,
                startX,
                startY,
                endX,
                endY,
                durationMilliseconds,
                cancellationToken);
        }

        return ExecuteIosInputAsync(
            "swipe",
            deviceIdentifier,
            async () =>
            {
                var viewport = await RequireIosViewportAsync(deviceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return await driver.SwipeAsync(
                        CreateSwipeRequest(
                            deviceIdentifier,
                            viewport,
                            startX,
                            startY,
                            endX,
                            endY,
                            durationMilliseconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    public Task<DeviceOperationResult> SendPinchAsync(
        string platform,
        string deviceIdentifier,
        int centerX,
        int centerY,
        int startSpan,
        int endSpan,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            return Task.FromResult(DeviceOperationResult.Failure(
                "pinch",
                normalizedPlatform,
                deviceIdentifier,
                "Android ADB input does not support multi-touch pinch gestures."));
        }

        return ExecuteIosInputAsync(
            "pinch",
            deviceIdentifier,
            async () =>
            {
                var viewport = await RequireIosViewportAsync(deviceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return await driver.PinchAsync(
                        CreatePinchRequest(
                            deviceIdentifier,
                            viewport,
                            centerX,
                            centerY,
                            startSpan,
                            endSpan,
                            durationMilliseconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    public Task<DeviceOperationResult> SendTextAsync(
        string platform,
        string deviceIdentifier,
        string value,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            return devices.SendTextAsync(
                normalizedPlatform,
                deviceIdentifier,
                value,
                cancellationToken);
        }

        return ExecuteIosInputAsync(
            "type-text",
            deviceIdentifier,
            async () =>
            {
                await RequireIosDeviceAsync(deviceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return await driver.TypeTextAsync(
                        new UiTextRequest(
                            InputSessionId,
                            deviceIdentifier,
                            value,
                            ReplaceExisting: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    public Task<DeviceOperationResult> SendButtonAsync(
        string platform,
        string deviceIdentifier,
        string button,
        CancellationToken cancellationToken)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform == DevicePlatforms.Android)
        {
            return devices.SendButtonAsync(
                normalizedPlatform,
                deviceIdentifier,
                button,
                cancellationToken);
        }

        return ExecuteIosInputAsync(
            "press-button",
            deviceIdentifier,
            async () =>
            {
                await RequireIosDeviceAsync(deviceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return await driver.PressButtonAsync(
                        new UiButtonRequest(InputSessionId, deviceIdentifier, button),
                        cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        driver.Dispose();
    }

    internal static UiTapRequest CreateTapRequest(
        string deviceIdentifier,
        UiViewport viewport,
        double x,
        double y)
        => new(
            InputSessionId,
            deviceIdentifier,
            ToNormalizedCoordinate(x, viewport.Width, "x", viewport.CoordinateUnit),
            ToNormalizedCoordinate(y, viewport.Height, "y", viewport.CoordinateUnit));

    internal static UiSwipeRequest CreateSwipeRequest(
        string deviceIdentifier,
        UiViewport viewport,
        double startX,
        double startY,
        double endX,
        double endY,
        int durationMilliseconds)
        => new(
            InputSessionId,
            deviceIdentifier,
            ToNormalizedCoordinate(startX, viewport.Width, "start-x", viewport.CoordinateUnit),
            ToNormalizedCoordinate(startY, viewport.Height, "start-y", viewport.CoordinateUnit),
            ToNormalizedCoordinate(endX, viewport.Width, "end-x", viewport.CoordinateUnit),
            ToNormalizedCoordinate(endY, viewport.Height, "end-y", viewport.CoordinateUnit),
            durationMilliseconds);

    internal static UiPinchRequest CreatePinchRequest(
        string deviceIdentifier,
        UiViewport viewport,
        double centerX,
        double centerY,
        double startSpan,
        double endSpan,
        int durationMilliseconds)
    {
        var startRadius = startSpan / 2;
        var endRadius = endSpan / 2;
        var normalizedY = ToNormalizedCoordinate(
            centerY,
            viewport.Height,
            "center-y",
            viewport.CoordinateUnit);
        return new UiPinchRequest(
            InputSessionId,
            deviceIdentifier,
            ToNormalizedCoordinate(
                centerX - startRadius,
                viewport.Width,
                "primary-start-x",
                viewport.CoordinateUnit),
            normalizedY,
            ToNormalizedCoordinate(
                centerX + startRadius,
                viewport.Width,
                "secondary-start-x",
                viewport.CoordinateUnit),
            normalizedY,
            ToNormalizedCoordinate(
                centerX - endRadius,
                viewport.Width,
                "primary-end-x",
                viewport.CoordinateUnit),
            normalizedY,
            ToNormalizedCoordinate(
                centerX + endRadius,
                viewport.Width,
                "secondary-end-x",
                viewport.CoordinateUnit),
            normalizedY,
            durationMilliseconds);
    }

    internal static double ToNormalizedCoordinate(
        double coordinate,
        double extent,
        string optionName,
        string coordinateUnit)
    {
        if (!double.IsFinite(extent) || extent <= 0)
        {
            throw new InvalidOperationException("The target display extent is invalid.");
        }

        var maximum = Math.Max(0, extent - 1);
        if (!double.IsFinite(coordinate) || coordinate < 0 || coordinate > maximum)
        {
            throw new CliUsageException(
                $"--{optionName} must be between 0 and {maximum:0.###} {coordinateUnit} for the target display.");
        }

        return maximum == 0 ? 0 : coordinate / maximum;
    }

    private async Task<UiViewport> RequireIosViewportAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var viewport = await driver.GetViewportAsync(deviceIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(viewport.Platform, DevicePlatforms.Ios, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Device '{deviceIdentifier}' is '{viewport.Platform}', not 'ios'.");
        }

        return viewport;
    }

    private async System.Threading.Tasks.Task RequireIosDeviceAsync(
        string deviceIdentifier,
        CancellationToken cancellationToken)
    {
        var platform = await driver.GetDevicePlatformAsync(deviceIdentifier, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(platform, DevicePlatforms.Ios, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Device '{deviceIdentifier}' is '{platform}', not 'ios'.");
        }
    }

    private static async Task<DeviceOperationResult> ExecuteIosInputAsync(
        string operation,
        string deviceIdentifier,
        Func<Task<UiInputResult>> action)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return new DeviceOperationResult(
                result.IsSuccess,
                operation,
                DevicePlatforms.Ios,
                deviceIdentifier,
                result.Message);
        }
        catch (Exception exception) when (exception is not CliUsageException and not OperationCanceledException)
        {
            return DeviceOperationResult.Failure(
                operation,
                DevicePlatforms.Ios,
                deviceIdentifier,
                exception.Message);
        }
    }

    private static string NormalizePlatform(string platform)
        => string.IsNullOrWhiteSpace(platform)
            ? throw new CliUsageException("An input platform is required.")
            : platform.Trim().ToLowerInvariant() switch
            {
                DevicePlatforms.Android => DevicePlatforms.Android,
                DevicePlatforms.Ios => DevicePlatforms.Ios,
                _ => throw new CliUsageException(
                    $"Unsupported input platform '{platform}'. Expected ios or android.")
            };
}
