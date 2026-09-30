namespace Ansight.Host.Devices;

public sealed record DeviceOperationResult(
    bool IsSuccess,
    string Operation,
    string Platform,
    string DeviceIdentifier,
    string Message,
    string? OutputPath = null)
{
    public static DeviceOperationResult Success(
        string operation,
        string platform,
        string deviceIdentifier,
        string message,
        string? outputPath = null)
        => new(true, operation, platform, deviceIdentifier, message, outputPath);

    public static DeviceOperationResult Failure(
        string operation,
        string platform,
        string deviceIdentifier,
        string message)
        => new(false, operation, platform, deviceIdentifier, message);
}
