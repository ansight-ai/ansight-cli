using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ansight.Host.Identity;

internal sealed record HardwareMachineFingerprint(
    string Value,
    string Version,
    string Source);

internal static partial class HardwareMachineFingerprintProvider
{
    private const string FingerprintVersion = "hardware-sha256-v1";
    private static readonly Lazy<HardwareMachineFingerprint> CurrentFingerprint = new(ResolveCore);

    public static HardwareMachineFingerprint Current => CurrentFingerprint.Value;

    internal static HardwareMachineFingerprint Create(string source, string hardwareIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareIdentifier);

        var normalizedSource = source.Trim().ToLowerInvariant();
        var normalizedIdentifier = hardwareIdentifier.Trim().ToLowerInvariant();
        var payload = $"ansight-companion-hardware-v1\0{normalizedSource}\0{normalizedIdentifier}";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new HardwareMachineFingerprint(fingerprint, FingerprintVersion, normalizedSource);
    }

    private static HardwareMachineFingerprint ResolveCore()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Create("macos.ioplatformuuid", ReadMacPlatformUuid());
        }

        if (OperatingSystem.IsWindows())
        {
            return Create("windows.machineguid", ReadWindowsMachineGuid());
        }

        if (OperatingSystem.IsLinux())
        {
            return Create("linux.machine-id", ReadLinuxMachineId());
        }

        throw new PlatformNotSupportedException(
            "Machine registration requires a supported hardware fingerprint source on macOS, Windows, or Linux.");
    }

    private static string ReadMacPlatformUuid()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/sbin/ioreg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-rd1");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("IOPlatformExpertDevice");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ioreg to read the macOS hardware identity.");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out while reading the macOS hardware identity.");
        }
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "ioreg could not read the macOS hardware identity."
                    : $"ioreg could not read the macOS hardware identity: {error}");
        }

        var match = MacPlatformUuidRegex().Match(output);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value
            : throw new InvalidOperationException("macOS did not report an IOPlatformUUID for machine registration.");
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        return key?.GetValue("MachineGuid") is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("Windows did not report a MachineGuid for machine registration.");
    }

    private static string ReadLinuxMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var value = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException("Linux did not report a machine-id for machine registration.");
    }

    [GeneratedRegex("\\\"IOPlatformUUID\\\"\\s*=\\s*\\\"([^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex MacPlatformUuidRegex();
}
