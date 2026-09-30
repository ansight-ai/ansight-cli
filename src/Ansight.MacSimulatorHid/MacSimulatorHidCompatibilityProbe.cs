using Ansight.MacSimulatorHid.Interop;

namespace Ansight.MacSimulatorHid;

public static class MacSimulatorHidCompatibilityProbe
{
    private const int ErrorBufferSize = 2048;

    public static MacSimulatorHidCompatibility Check(string developerDirectory)
        => Check(
            developerDirectory,
            OperatingSystem.IsMacOS(),
            ReadCompatibilityFailure);

    internal static MacSimulatorHidCompatibility Check(
        string developerDirectory,
        bool isMacOS,
        Func<string, string?> compatibilityCheck)
    {
        ArgumentNullException.ThrowIfNull(compatibilityCheck);
        if (!isMacOS)
        {
            return Unavailable(
                "unsupported-platform",
                "Native SimulatorKit HID input is available only on macOS.",
                developerDirectory: null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(developerDirectory);
        var normalizedDeveloperDirectory = Path.GetFullPath(developerDirectory.Trim());
        try
        {
            var failure = compatibilityCheck(normalizedDeveloperDirectory);
            if (!string.IsNullOrWhiteSpace(failure))
            {
                throw new MacSimulatorHidException(failure);
            }

            return new MacSimulatorHidCompatibility(
                true,
                "available",
                "The native SimulatorKit HID binding loaded successfully for the selected Xcode installation.",
                normalizedDeveloperDirectory);
        }
        catch (Exception exception) when (IsCompatibilityFailure(exception.GetBaseException()))
        {
            var rootException = exception.GetBaseException();
            return Unavailable(
                ResolveFailureStatus(rootException),
                $"The native SimulatorKit HID binding is unavailable: {rootException.Message}",
                normalizedDeveloperDirectory);
        }
    }

    private static string? ReadCompatibilityFailure(string developerDirectory)
    {
        var errorBuffer = new byte[ErrorBufferSize];
        var isCompatible = NativeMethods.CheckCompatibility(
            developerDirectory,
            errorBuffer,
            (nuint)errorBuffer.Length);
        if (isCompatible)
        {
            return null;
        }

        var length = Array.IndexOf(errorBuffer, (byte)0);
        if (length < 0)
        {
            length = errorBuffer.Length;
        }
        var message = System.Text.Encoding.UTF8.GetString(errorBuffer, 0, length).Trim();
        return string.IsNullOrWhiteSpace(message)
            ? "The native SimulatorKit HID compatibility check failed."
            : message;
    }

    private static bool IsCompatibilityFailure(Exception exception)
        => exception is MacSimulatorHidException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException;

    private static string ResolveFailureStatus(Exception exception)
        => exception switch
        {
            DllNotFoundException => "bridge-missing",
            EntryPointNotFoundException => "bridge-incompatible",
            BadImageFormatException => "architecture-mismatch",
            UnauthorizedAccessException => "permission-denied",
            PlatformNotSupportedException => "unsupported-platform",
            MacSimulatorHidException simulatorException => IsXcodeCompatibilityFailure(simulatorException.Message)
                ? "xcode-incompatible"
                : "runtime-unavailable",
            _ => "unavailable"
        };

    private static bool IsXcodeCompatibilityFailure(string message)
        => message.Contains("Could not load CoreSimulator", StringComparison.OrdinalIgnoreCase)
           || message.Contains("Could not load SimulatorKit", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not expose the required", StringComparison.OrdinalIgnoreCase)
           || message.Contains("service context is unavailable", StringComparison.OrdinalIgnoreCase)
           || message.Contains("SimDeviceLegacyHIDClient is unavailable", StringComparison.OrdinalIgnoreCase)
           || message.Contains("has no supported", StringComparison.OrdinalIgnoreCase);

    private static MacSimulatorHidCompatibility Unavailable(
        string status,
        string message,
        string? developerDirectory)
        => new(false, status, message, developerDirectory);
}
