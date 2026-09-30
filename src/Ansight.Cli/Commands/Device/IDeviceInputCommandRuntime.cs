using Ansight.Host;

namespace Ansight.Cli.Commands.Device;

internal interface IDeviceInputCommandRuntime
{
    Task<DeviceOperationResult> SendTapAsync(
        string platform,
        string deviceIdentifier,
        int x,
        int y,
        CancellationToken cancellationToken);

    Task<DeviceOperationResult> SendSwipeAsync(
        string platform,
        string deviceIdentifier,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        CancellationToken cancellationToken);

    Task<DeviceOperationResult> SendPinchAsync(
        string platform,
        string deviceIdentifier,
        int centerX,
        int centerY,
        int startSpan,
        int endSpan,
        int durationMilliseconds,
        CancellationToken cancellationToken);

    Task<DeviceOperationResult> SendTextAsync(
        string platform,
        string deviceIdentifier,
        string value,
        CancellationToken cancellationToken);

    Task<DeviceOperationResult> SendButtonAsync(
        string platform,
        string deviceIdentifier,
        string button,
        CancellationToken cancellationToken);
}
