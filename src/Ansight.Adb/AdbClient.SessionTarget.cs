namespace Ansight.Adb;

public sealed partial class AdbClient
{
    /// <summary>Resolves an SDK identity to one connected device, verifying its app process.</summary>
    public async Task<AdbSessionTargetResolution> ResolveSessionTargetAsync(
        string? reportedIdentifier,
        string packageIdentifier,
        int? processId,
        CancellationToken cancellationToken = default)
    {
        ValidatePackageIdentifier(packageIdentifier);
        var identifier = reportedIdentifier?.Trim();
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var connected = devices.Where(device => device.IsConnected).ToArray();
        var candidates = new List<AdbDevice>();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            candidates.AddRange(connected);
        }
        else if (devices.Any(device => string.Equals(device.Serial, identifier, StringComparison.Ordinal))
                 || identifier.StartsWith("emulator-", StringComparison.Ordinal))
        {
            // An explicit serial must never fall back to another device running the same app.
            candidates.AddRange(connected.Where(device => device.Serial == identifier));
        }
        else
        {
            // SDKs can read the AVD name, but the host owns its current emulator-NNNN serial.
            foreach (var device in connected.Where(device => device.Serial.StartsWith("emulator-", StringComparison.Ordinal)))
            {
                var avdName = await GetEmulatorAvdNameAsync(device.Serial, cancellationToken).ConfigureAwait(false);
                if (string.Equals(avdName, identifier, StringComparison.Ordinal))
                    candidates.Add(device);
            }
        }

        var matches = new List<AdbDevice>();
        foreach (var device in candidates)
        {
            var processes = await GetProcessIdsAsync(device.Serial, packageIdentifier, cancellationToken).ConfigureAwait(false);
            if (processes.Count > 0 && (!processId.HasValue || processes.Contains(processId.Value)))
                matches.Add(device);
        }

        if (matches.Count == 1)
            return new(matches[0], $"Bound SDK session to ADB device '{matches[0].Serial}'.");

        var identity = string.IsNullOrWhiteSpace(identifier) ? "unspecified" : identifier;
        var process = processId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unspecified";
        var detail = $"SDK device '{identity}', package '{packageIdentifier}', process '{process}'. "
                     + $"Connected ADB devices: {string.Join(", ", connected.Select(device => device.Serial))}.";
        return new(null, matches.Count > 1
            ? $"More than one ADB device matched the session; refusing an ambiguous binding. {detail}"
            : $"No connected ADB device matched the session identity and app process. {detail}")
        {
            Code = matches.Count > 1 ? "ambiguous-device" : "device-not-matched",
            CandidateDeviceIds = candidates.Select(device => device.Serial).ToArray(),
            Recovery = matches.Count > 1
                ? "Stop the duplicate app instance, then reconnect this app. Use 'ansight device list' to identify each emulator."
                : "Run 'ansight device list', authorize or start the intended device, and relaunch the app so its process identity is refreshed."
        };
    }
}

public sealed record AdbSessionTargetResolution(AdbDevice? Device, string Message)
{
    public string Code { get; init; } = "bound";
    public IReadOnlyList<string> CandidateDeviceIds { get; init; } = [];
    public string? Recovery { get; init; }
}
