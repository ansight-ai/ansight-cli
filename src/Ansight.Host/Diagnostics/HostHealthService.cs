using System.Reflection;
using System.Runtime.InteropServices;
using Ansight.Adb;
using SkiaSharp;

namespace Ansight.Host.Diagnostics;

public sealed class HostHealthService
{
    private static readonly string[] optionalExecutables =
    [
        "dotnet-trace",
        "dotnet-dsrouter",
        "node",
        "tesseract",
        "appium"
    ];

    private readonly RuntimeCoordinator runtime;

    internal HostHealthService(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<HostHealthSnapshot> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = new List<HostHealthCheck>
        {
            CheckDataDirectory(),
            CheckNativeImageRuntime(),
            CheckMacPermission("permission.screen-recording", MacNativePermissions.HasScreenRecordingAccess),
            CheckMacPermission("permission.accessibility", MacNativePermissions.HasAccessibilityAccess)
        };
        checks.AddRange(optionalExecutables.Select(executable => CheckExecutable(executable, false)));

        var scrcpy = await ScrcpyToolLocator.ResolveAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        checks.Add(new HostHealthCheck(
            "tool.scrcpy",
            scrcpy.IsFound ? "available" : "unavailable",
            scrcpy.IsFound,
            false,
            scrcpy.Message,
            scrcpy.IsFound ? scrcpy.ExecutablePath : null));

        var deviceCapabilities = await runtime.Devices.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        checks.AddRange(deviceCapabilities.Select(capability => new HostHealthCheck(
            $"device.{capability.Platform}",
            capability.Status ?? (capability.IsAvailable ? "available" : "unavailable"),
            capability.IsAvailable,
            false,
            capability.Message,
            string.IsNullOrWhiteSpace(capability.Backend) ? null : capability.Backend)));

        var companion = runtime.ActiveCompanion?.GetAccessStatus() ?? new CompanionAccessStatus(CompanionAccessMode.Disabled, false, false, "Optional cloud extension is inactive.", 0);
        checks.Add(new HostHealthCheck(
            "companion.remote-control",
            companion.IsAvailable ? "available" : "unavailable",
            companion.IsAvailable,
            false,
            companion.Status,
            null));
        var webRtcControlUrl = runtime.LocalSimulatorControl?.LoopbackBaseUrl;
        var hasWebRtcControl = !string.IsNullOrWhiteSpace(webRtcControlUrl);
        checks.Add(new HostHealthCheck(
            "native.webrtc-control",
            hasWebRtcControl ? "available" : "unavailable",
            hasWebRtcControl,
            false,
            hasWebRtcControl
                ? "The local WebRTC companion control service is running."
                : "The local WebRTC companion control service is not running.",
            hasWebRtcControl ? webRtcControlUrl : null));

        return new HostHealthSnapshot(
            "ansight.host-health/v1",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Environment.Version.ToString(),
            ResolveHostVersion(),
            runtime.BaseFolderPath,
            runtime.ApplicationPaths.ApplicationLogsPath,
            checks,
            checks.All(static check => check.IsSuccess || !check.IsRequired),
            DateTimeOffset.UtcNow);
    }

    private static string ResolveHostVersion()
        => ResolveHostVersion(Assembly.GetEntryAssembly());

    internal static string ResolveHostVersion(Assembly? entryAssembly)
    {
        var entryAssemblyName = entryAssembly?.GetName().Name;
        if (entryAssembly is not null
            && (string.Equals(entryAssemblyName, "ansight", StringComparison.OrdinalIgnoreCase)
                || entryAssemblyName?.StartsWith("Ansight.", StringComparison.OrdinalIgnoreCase) == true))
        {
            return ResolveAssemblyProductVersion(entryAssembly);
        }

        return ResolveAssemblyProductVersion(typeof(RuntimeCoordinator).Assembly);
    }

    internal static string ResolveAssemblyProductVersion(Assembly assembly)
    {
        var productVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute => string.Equals(
                attribute.Key,
                "AnsightCliVersion",
                StringComparison.Ordinal))?
            .Value;
        if (!string.IsNullOrWhiteSpace(productVersion))
        {
            return productVersion;
        }

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private HostHealthCheck CheckDataDirectory()
    {
        var probePath = Path.Combine(runtime.BaseFolderPath, $".ansight-write-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(runtime.BaseFolderPath);
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return new HostHealthCheck(
                "data-directory",
                "available",
                true,
                true,
                "The Ansight data directory is writable.",
                runtime.BaseFolderPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostHealthCheck(
                "data-directory",
                "unavailable",
                false,
                true,
                exception.Message,
                runtime.BaseFolderPath);
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static HostHealthCheck CheckNativeImageRuntime()
    {
        try
        {
            using var data = SKData.CreateCopy([0]);
            return new HostHealthCheck(
                "native.skia",
                "available",
                true,
                false,
                "The SkiaSharp native runtime is available.",
                "libSkiaSharp");
        }
        catch (Exception exception)
        {
            return new HostHealthCheck(
                "native.skia",
                "unavailable",
                false,
                false,
                $"The SkiaSharp native runtime is unavailable: {exception.GetBaseException().Message}",
                null);
        }
    }

    private static HostHealthCheck CheckMacPermission(string name, Func<bool> probe)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new HostHealthCheck(
                name,
                "not-applicable",
                true,
                false,
                "This macOS privacy permission is not applicable on the current operating system.",
                null);
        }

        try
        {
            var isGranted = probe();
            return new HostHealthCheck(
                name,
                isGranted ? "granted" : "not-granted",
                isGranted,
                false,
                isGranted
                    ? "The host process has this macOS privacy permission."
                    : "Grant this permission in System Settings > Privacy & Security when native capture or input requires it.",
                "System Settings > Privacy & Security");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new HostHealthCheck(
                name,
                "not-checked",
                false,
                false,
                $"The permission could not be checked: {exception.GetBaseException().Message}",
                null);
        }
    }

    private static HostHealthCheck CheckExecutable(string executableName, bool required)
    {
        var path = FindExecutable(executableName);
        return new HostHealthCheck(
            $"tool.{executableName}",
            path is null ? "unavailable" : "available",
            path is not null,
            required,
            path is null
                ? $"'{executableName}' was not found on PATH."
                : $"'{executableName}' is available.",
            path);
    }

    private static string? FindExecutable(string executableName)
    {
        var effectiveName = OperatingSystem.IsWindows()
                            && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName + ".exe"
            : executableName;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), effectiveName))
            .FirstOrDefault(File.Exists) is { } resolvedPath
            ? Path.GetFullPath(resolvedPath)
            : null;
    }
}

internal static class MacNativePermissions
{
    [DllImport(
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
        EntryPoint = "CGPreflightScreenCaptureAccess")]
    private static extern byte PreflightScreenCaptureAccess();

    [DllImport(
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
        EntryPoint = "AXIsProcessTrusted")]
    private static extern byte IsProcessTrusted();

    public static bool HasScreenRecordingAccess() => PreflightScreenCaptureAccess() != 0;

    public static bool HasAccessibilityAccess() => IsProcessTrusted() != 0;
}

public sealed record HostHealthSnapshot(
    string Schema,
    string OperatingSystem,
    string Architecture,
    string DotNetVersion,
    string HostVersion,
    string DataDirectory,
    string LogDirectory,
    IReadOnlyList<HostHealthCheck> Checks,
    bool IsHealthy,
    DateTimeOffset GeneratedAtUtc)
{
    public string Signal => !IsHealthy
        ? "red"
        : Checks.All(static check => check.IsSuccess)
            ? "green"
            : "amber";
}

public sealed record HostHealthCheck(
    string Name,
    string Status,
    bool IsSuccess,
    bool IsRequired,
    string Message,
    string? Path)
{
    public string Signal => IsSuccess ? "green" : IsRequired ? "red" : "amber";
}
