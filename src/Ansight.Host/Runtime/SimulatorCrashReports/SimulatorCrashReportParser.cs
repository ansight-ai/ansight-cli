namespace Ansight.Host.Runtime.SimulatorCrashReports;

using System.Globalization;
using System.Text.Json;

internal static class SimulatorCrashReportParser
{
    private const string CoreSimulatorDevicePathMarker = "/CoreSimulator/Devices/";
    private const string CoreSimulatorCoalitionPrefix = "com.apple.CoreSimulator.SimDevice.";

    public static bool TryParse(string filePath, out SimulatorCrashReport? report)
    {
        report = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var reader = new StreamReader(filePath);
            var headerJson = reader.ReadLine();
            var bodyJson = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(headerJson) || string.IsNullOrWhiteSpace(bodyJson))
            {
                return false;
            }

            using var headerDocument = JsonDocument.Parse(headerJson);
            using var bodyDocument = JsonDocument.Parse(bodyJson);
            var header = headerDocument.RootElement;
            var body = bodyDocument.RootElement;
            if (header.ValueKind != JsonValueKind.Object || body.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var processPath = ReadString(body, "procPath");
            var coalitionName = ReadString(body, "coalitionName");
            var hasFatalDiagnostic = TryGetObject(body, "exception").HasValue
                                     || TryGetObject(body, "termination").HasValue;
            var isSimulator = ReadInt32(header, "is_simulated") == 1
                              || ContainsCoreSimulatorDevicePath(processPath)
                              || coalitionName?.StartsWith(CoreSimulatorCoalitionPrefix, StringComparison.Ordinal) == true;
            if (!isSimulator || !hasFatalDiagnostic)
            {
                return false;
            }

            var bundleInfo = TryGetObject(body, "bundleInfo");
            var bundleId = FirstNonEmpty(
                bundleInfo.HasValue ? ReadString(bundleInfo.Value, "CFBundleIdentifier") : null,
                ReadString(header, "bundleID"));
            var incidentId = FirstNonEmpty(ReadString(body, "incident"), ReadString(header, "incident_id"));
            var processName = FirstNonEmpty(ReadString(body, "procName"), ReadString(header, "app_name"), ReadString(header, "name"));
            var processId = ReadInt32(body, "pid");
            var capturedAt = ParseDateTimeOffset(ReadString(body, "captureTime"))
                             ?? ParseDateTimeOffset(ReadString(header, "timestamp"));

            if (string.IsNullOrWhiteSpace(bundleId)
                || string.IsNullOrWhiteSpace(incidentId)
                || string.IsNullOrWhiteSpace(processName)
                || !processId.HasValue
                || processId.Value <= 0
                || !capturedAt.HasValue)
            {
                return false;
            }

            report = new SimulatorCrashReport(
                Path.GetFullPath(filePath),
                incidentId,
                bundleId,
                processId.Value,
                capturedAt.Value.ToUniversalTime(),
                processName,
                FirstNonEmpty(
                    bundleInfo.HasValue ? ReadString(bundleInfo.Value, "CFBundleShortVersionString") : null,
                    ReadString(header, "app_version")),
                FirstNonEmpty(
                    bundleInfo.HasValue ? ReadString(bundleInfo.Value, "CFBundleVersion") : null,
                    ReadString(header, "build_version")),
                ResolveSimulatorUdid(processPath, coalitionName));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static JsonElement? TryGetObject(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(value.GetString())
            : null;
    }

    private static int? ReadInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericValue))
        {
            return numericValue;
        }

        return value.ValueKind == JsonValueKind.String
               && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textValue)
            ? textValue
            : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? ResolveSimulatorUdid(string? processPath, string? coalitionName)
    {
        if (ContainsCoreSimulatorDevicePath(processPath))
        {
            var start = processPath!.IndexOf(CoreSimulatorDevicePathMarker, StringComparison.Ordinal)
                        + CoreSimulatorDevicePathMarker.Length;
            var end = processPath.IndexOf('/', start);
            var candidate = end > start ? processPath[start..end] : processPath[start..];
            if (Guid.TryParse(candidate, out var udid))
            {
                return udid.ToString("D").ToUpperInvariant();
            }
        }

        if (coalitionName?.StartsWith(CoreSimulatorCoalitionPrefix, StringComparison.Ordinal) == true)
        {
            var candidate = coalitionName[CoreSimulatorCoalitionPrefix.Length..];
            if (Guid.TryParse(candidate, out var udid))
            {
                return udid.ToString("D").ToUpperInvariant();
            }
        }

        return null;
    }

    private static bool ContainsCoreSimulatorDevicePath(string? value)
        => value?.Contains(CoreSimulatorDevicePathMarker, StringComparison.Ordinal) == true;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
