using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Ansight.Adb;

namespace Ansight.Cli.Commands.Setup;

internal sealed record AndroidSetupPlan(
    string Target,
    string SdkRoot,
    string AdbPath,
    string? SdkManager,
    string? AvdManager,
    string? JavaPath,
    string AvdName,
    IReadOnlyList<string> MissingPackages,
    bool CreateAvd,
    IReadOnlyList<string> Blockers)
{
    public const string BaselineImage = "system-images;android-35;google_apis;x86_64";
    public const string CommandToolsPackage = "cmdline-tools;19.0";
    public bool NeedsChanges => MissingPackages.Count > 0 || CreateAvd;

    internal static AndroidSetupPlan Discover(CliArguments arguments, string? configuredAdb, string? avdHomeOverride = null)
    {
        var target = arguments.GetOption("target") ?? "android-device";
        if (target is not ("android-device" or "android-emulator")
            || (arguments.HasFlag("target") && arguments.GetOption("target") is null))
            throw new CliUsageException("--target requires android-device or android-emulator.");
        var explicitRoot = arguments.GetOption("sdk-root");
        if (arguments.HasFlag("sdk-root") && string.IsNullOrWhiteSpace(explicitRoot))
            throw new CliUsageException("--sdk-root requires a path.");
        var avdName = arguments.GetOption("avd-name") ?? "ansight-api35";
        if (!Regex.IsMatch(avdName, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$"))
            throw new CliUsageException("--avd-name must be a simple name containing letters, digits, dots, underscores, or hyphens.");
        var adb = AdbToolLocator.Resolve(configuredAdb);
        var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : configuredAdb);
        var root = explicitRoot
            ?? (adb.IsFound ? AndroidSdkDiscovery.FromTool(adb.AdbPath) : null)
            ?? (emulator.IsFound ? AndroidSdkDiscovery.FromTool(emulator.EmulatorPath) : null)
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? AndroidSdkDiscovery.FromPath(Environment.GetEnvironmentVariable("PATH")).FirstOrDefault()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Android", "Sdk");
        root = Path.GetFullPath(root);
        var adbPath = explicitRoot is null && target == "android-device" && adb.IsFound
            ? adb.AdbPath : Path.Combine(root, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        var sdkManager = FindManager(root, "sdkmanager");
        var avdManager = FindManager(root, "avdmanager");
        var java = FindJava();
        var missing = new List<string>();
        if (!File.Exists(adbPath)) missing.Add("platform-tools");
        var avdHome = avdHomeOverride ?? ResolveAvdHome();
        var createAvd = false;
        if (target == "android-emulator")
        {
            if (!File.Exists(Path.Combine(root, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator"))) missing.Add("emulator");
            // Reuse any existing AVD whose image is installed; never replace it just to match our baseline.
            var names = Directory.Exists(avdHome)
                ? Directory.EnumerateFiles(avdHome, "*.ini").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray()
                : [];
            createAvd = !AndroidReadinessDoctor.CheckSystemImages(names, avdHome, root).IsSuccess;
            if (createAvd && !File.Exists(Path.Combine(root, BaselineImage.Replace(';', Path.DirectorySeparatorChar), "system.img")))
                missing.Add(BaselineImage);
            if ((missing.Count > 0 || createAvd) && (sdkManager is null || avdManager is null))
                missing.Insert(0, CommandToolsPackage);
        }
        var blockers = new List<string>();
        if (target == "android-emulator" && createAvd
            && (File.Exists(Path.Combine(avdHome, avdName + ".ini")) || Directory.Exists(Path.Combine(avdHome, avdName + ".avd"))))
            blockers.Add($"AVD '{avdName}' already exists but its image is unavailable. Repair that AVD or choose a different --avd-name; setup will not overwrite it.");
        if (missing.Count > 0 || createAvd)
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
                blockers.Add("Automatic Android installation currently supports Linux x64. Existing installations can still be discovered and used on other hosts.");
            if (target == "android-emulator")
            {
                if (java is null) blockers.Add("Java is required for Android SDK command-line tools. Install JDK 17 or newer (for Ubuntu: sudo apt-get install openjdk-17-jdk-headless), or set JAVA_HOME to an existing JDK.");
                if (OperatingSystem.IsLinux())
                {
                    if (File.Exists("/proc/meminfo"))
                    {
                        var memory = Regex.Match(File.ReadAllText("/proc/meminfo"), @"(?m)^MemTotal:\s+(\d+)\s+kB");
                        if (memory.Success && long.Parse(memory.Groups[1].Value) < 4L * 1024 * 1024)
                            blockers.Add("The baseline Android emulator setup requires at least 4 GiB of RAM. Increase VM memory or use an external Android target.");
                    }
                    var kvm = AndroidReadinessDoctor.CheckKvm("/dev/kvm");
                    if (!kvm.IsSuccess) blockers.Add(kvm.Message);
                }
            }
        }
        return new(target, root, adbPath, sdkManager, avdManager, java, avdName, missing, createAvd, blockers);
    }

    internal static string ResolveAvdHome()
        => Environment.GetEnvironmentVariable("ANDROID_AVD_HOME")
           ?? Path.Combine(Environment.GetEnvironmentVariable("ANDROID_USER_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android"), "avd");

    internal static string? FindManager(string sdkRoot, string name)
    {
        var executable = name + (OperatingSystem.IsWindows() ? ".bat" : string.Empty);
        var toolsRoot = Path.Combine(sdkRoot, "cmdline-tools");
        if (Directory.Exists(toolsRoot))
        {
            var preferred = Path.Combine(toolsRoot, "latest", "bin", executable);
            if (File.Exists(preferred)) return preferred;
            foreach (var directory in Directory.EnumerateDirectories(toolsRoot).OrderDescending(StringComparer.Ordinal))
            {
                var path = Path.Combine(directory, "bin", executable);
                if (File.Exists(path)) return path;
            }
        }
        var legacy = Path.Combine(sdkRoot, "tools", "bin", executable);
        return File.Exists(legacy) ? legacy : null;
    }

    private static string? FindJava()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome) && File.Exists(Path.Combine(javaHome, "bin", name)))
            return Path.Combine(javaHome, "bin", name);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        foreach (var root in new[] { "/opt/android-studio/jbr", "/usr/local/android-studio/jbr" })
            if (File.Exists(Path.Combine(root, "bin", name))) return Path.Combine(root, "bin", name);
        return null;
    }
}
