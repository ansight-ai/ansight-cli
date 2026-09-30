using System.Diagnostics;
using System.Text.RegularExpressions;
using Ansight.Adb;

namespace Ansight.Cli.Commands.Doctor;

internal static class AndroidReadinessDoctor
{
    public static async Task<IReadOnlyList<DoctorCheck>> CheckAsync(string target, string? adbPath, CancellationToken token)
    {
        var required = target != "auto";
        var checks = new List<DoctorCheck>();
        var adb = AdbToolLocator.Resolve(adbPath);
        var sdkProbe = adb.IsFound ? await RunAsync(adb.AdbPath, ["version"], token) : new ProbeResult(false, adb.Message);
        checks.Add(new("android.sdk", sdkProbe.Success ? "available" : "unavailable", sdkProbe.Success, required,
            sdkProbe.Success ? adb.Message : "ADB could not run: " + sdkProbe.Output, adb.IsFound ? adb.AdbPath : null));
        if (target == "auto") return checks;
        if (target != "android-emulator")
        {
            var devices = adb.IsFound ? await RunAsync(adb.AdbPath, ["devices"], token) : new ProbeResult(false, "ADB is unavailable.");
            var connected = devices.Success && Regex.IsMatch(devices.Output, @"(?m)^\S+\s+device\s*$");
            checks.Add(new("android.connection", connected ? "connected" : "unavailable", connected, required,
                connected ? "An authorized Android device or emulator is connected. Screenshot and input capture have not been tested."
                    : "No authorized Android target is connected. Connect a device or external emulator through ADB and authorize this host. Local KVM and system images are not required. " + devices.Output, null));
            return checks;
        }

        var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : adbPath);
        checks.Add(new("android.emulator", emulator.IsFound ? "available" : "unavailable", emulator.IsFound, true, emulator.Message, emulator.IsFound ? emulator.EmulatorPath : null));
        if (OperatingSystem.IsLinux()) checks.Add(CheckKvm("/dev/kvm"));
        if (!emulator.IsFound)
        {
            checks.Add(new("android.system-image", "not-checked", false, true, "Install Android Emulator and a system image, then create an AVD with avdmanager.", null));
            checks.Add(new("android.acceleration", "not-checked", false, true, "Install Android Emulator to check hardware acceleration.", null));
            return checks;
        }
        var acceleration = await RunAsync(emulator.EmulatorPath, ["-accel-check"], token);
        checks.Add(CreateAccelerationCheck(acceleration.Success, acceleration.Output, emulator.EmulatorPath));
        var avds = await RunAsync(emulator.EmulatorPath, ["-list-avds"], token);
        var sdk = Directory.GetParent(Path.GetDirectoryName(new FileInfo(emulator.EmulatorPath).ResolveLinkTarget(true)?.FullName ?? emulator.EmulatorPath)!)!.FullName;
        var avdHome = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME")
            ?? Path.Combine(Environment.GetEnvironmentVariable("ANDROID_USER_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android"), "avd");
        checks.Add(CheckSystemImages(avds.Success ? avds.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [], avdHome, sdk));
        return checks;
    }

    internal static DoctorCheck CreateAccelerationCheck(bool processSucceeded, string output, string executable)
    {
        var usable = processSucceeded && Regex.IsMatch(output, @"(?m)^accel:\s*\r?\n0\s*\r?$");
        return new("android.acceleration", usable ? "available" : "unavailable", usable, true,
            usable ? "Android Emulator reports usable hardware acceleration."
                : "Android Emulator acceleration is unavailable. Inside a VM, enable nested virtualization on the outer host or use an external Android target. " + output, executable);
    }

    internal static DoctorCheck CheckSystemImages(IEnumerable<string> names, string avdHome, string sdk)
    {
        try { return InspectSystemImages(names, avdHome, sdk); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("android.system-image", "unavailable", false, true, "Could not inspect AVD system images: " + exception.Message, avdHome);
        }
    }

    private static DoctorCheck InspectSystemImages(IEnumerable<string> names, string avdHome, string sdk)
    {
        foreach (var name in names)
        {
            // emulator output is not a path; resolve the AVD registration explicitly.
            if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) continue;
            var registration = Path.Combine(avdHome, name + ".ini");
            var relative = ReadProperty(registration, "path.rel");
            var directory = ReadProperty(registration, "path")
                ?? (relative is null ? Path.Combine(avdHome, name + ".avd") : Path.GetFullPath(Path.Combine(avdHome, "..", relative)));
            var image = ReadProperty(Path.Combine(directory, "config.ini"), "image.sysdir.1");
            if (image is null) continue;
            var imagePath = Path.GetFullPath(Path.Combine(sdk, image));
            if (File.Exists(Path.Combine(imagePath, "system.img")) && new FileInfo(Path.Combine(imagePath, "system.img")).Length > 0)
                return new("android.system-image", "available", true, true, $"AVD '{name}' references an installed system image. Boot and UI capture have not been tested.", imagePath);
        }
        return new("android.system-image", "unavailable", false, true,
            "No configured AVD with an installed system.img was found. Install a compatible system image with sdkmanager and create an AVD with avdmanager.", avdHome);
    }

    private static string? ReadProperty(string path, string key)
        => File.Exists(path) ? File.ReadLines(path).Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim() == key).Select(parts => parts[1].Trim()).FirstOrDefault() : null;

    internal static DoctorCheck CheckKvm(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new("android.kvm", "accessible", true, true, "The current user can open /dev/kvm; emulator -accel-check separately verifies acceleration.", path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("android.kvm", "unavailable", false, true,
                "Cannot access /dev/kvm. Enable virtualization (and nested virtualization for a VM), load KVM, and grant this user access through the kvm group. Reconnect after changing group membership, or use an external Android target. " + exception.Message, path);
        }
    }

    private static async Task<ProbeResult> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                if (token.IsCancellationRequested) throw;
                return new(false, "Probe timed out after 15 seconds.");
            }
            return new(process.ExitCode == 0, (await stdout + "\n" + await stderr).Trim());
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, exception.Message);
        }
    }

    private sealed record ProbeResult(bool Success, string Output);
}
