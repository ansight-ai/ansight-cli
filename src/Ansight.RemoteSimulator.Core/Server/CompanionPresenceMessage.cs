using Ansight.RemoteSimulator.Core.Companion;

namespace Ansight.RemoteSimulator.Core.Server;

internal sealed record CompanionPresenceMessage(
    string? Kind,
    string? DeviceId,
    string? DeviceName,
    string? Model,
    string? Platform,
    string? OperatingSystemVersion,
    string? AppVersion)
{
    private const int MaximumIdentifierLength = 256;
    private const int MaximumDisplayValueLength = 512;

    public RemoteCompanionDevice? Normalize()
    {
        var identifier = NormalizeRequired(DeviceId, MaximumIdentifierLength);
        if (identifier is null)
        {
            return null;
        }

        var model = NormalizeOptional(Model, MaximumDisplayValueLength);
        var name = FirstNonEmpty(
            NormalizeOptional(DeviceName, MaximumDisplayValueLength),
            model,
            "Companion device");

        return new RemoteCompanionDevice(
            identifier,
            name,
            model,
            FirstNonEmpty(
                NormalizeOptional(Platform, MaximumDisplayValueLength),
                "Unknown platform"),
            NormalizeOptional(OperatingSystemVersion, MaximumDisplayValueLength),
            NormalizeOptional(AppVersion, MaximumDisplayValueLength));
    }

    private static string? NormalizeRequired(string? value, int maximumLength)
    {
        var normalized = NormalizeOptional(value, maximumLength);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static string FirstNonEmpty(params string[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value));
}
