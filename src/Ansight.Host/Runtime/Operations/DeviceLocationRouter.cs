using Ansight.Host;

namespace Ansight.Host.Runtime.Operations;

internal sealed class DeviceLocationRouter
{
    private const string UnavailableMessage =
        "This host has no device location driver. Use ansight device location set or clear with an explicit platform and device ID.";
    private readonly Lock gate = new();
    private IDeviceLocationDriver? driver;
    private DeviceLocationPlaybackService? playback;
    private IDeviceService? devices;

    public void ConfigurePlayback(DeviceLocationPlaybackService service, IDeviceService deviceService)
    {
        lock (gate)
        {
            playback = service;
            devices = deviceService;
        }
    }

    public async Task<DeviceLocationPlaybackStartResult> PlayLocationAsync(
        DeviceLocationPlaybackRequest request,
        CancellationToken cancellationToken)
    {
        DeviceLocationPlaybackService service;
        IDeviceService deviceService;
        lock (gate)
        {
            service = playback ?? throw new InvalidOperationException("This host has no location playback service.");
            deviceService = devices ?? throw new InvalidOperationException("This host has no device service.");
        }

        // Resolve the platform from the host inventory, never from a task-supplied platform.
        var inventory = await deviceService.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = inventory.Devices.Where(device =>
            string.Equals(device.Identifier, request.DeviceIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || !matches[0].IsBooted || !matches[0].IsVirtual
            || matches[0].Platform is not (DevicePlatforms.Ios or DevicePlatforms.Android))
        {
            throw new InvalidOperationException("Location playback requires a uniquely identified, booted simulator or emulator.");
        }

        return await service.StartAsync(request with
        {
            Platform = matches[0].Platform,
            DeviceIdentifier = matches[0].Identifier
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Configure(IDeviceLocationDriver? value)
    {
        lock (gate)
        {
            driver = value;
        }
    }

    public Task<DeviceLocationResult> SetLocationAsync(
        SetDeviceLocationRequest request,
        CancellationToken cancellationToken)
        => GetDriver()?.SetLocationAsync(request, cancellationToken)
           ?? Task.FromResult(DeviceLocationResult.Failure(UnavailableMessage));

    public Task<DeviceLocationResult> ClearLocationAsync(
        ClearDeviceLocationRequest request,
        CancellationToken cancellationToken)
        => GetDriver()?.ClearLocationAsync(request, cancellationToken)
           ?? Task.FromResult(DeviceLocationResult.Failure(UnavailableMessage));

    private IDeviceLocationDriver? GetDriver()
    {
        lock (gate)
        {
            return driver;
        }
    }
}
