using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Adb;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed partial class DevicePlatformProbe(WorkspaceTestTarget target, IDeviceCommandRunner commands, string? adbPath)
{
    private string? resolvedAdb;
    private string? appBundlePath;
    private string? androidFilePrefix;
    private string? androidRoot;
    private long clockTicks;
    public Ansight.Pairing.Models.DeviceApplicationIconProfile? AppIcon { get; private set; }
    public bool IsAndroid => target.Platform == DevicePlatforms.Android;
    public string Provider => IsAndroid ? "adb-process-v1" : "macos-rusage-v1";
    public string? FileProvider { get; private set; }
    public string? FileUnavailableReason { get; private set; }
    public JsonObject Metadata { get; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(target.ApplicationIdentifier, @"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_-]+)+$"))
            throw new ArgumentException("An exact application identifier is required.");
        if (IsAndroid)
        {
            var tool = AdbToolLocator.Resolve(adbPath);
            if (!tool.IsFound) throw new IOException(tool.Message);
            resolvedAdb = tool.AdbPath;
            var ticks = await AdbAsync("shell", "getconf CLK_TCK", cancellationToken).ConfigureAwait(false);
            // Missing CPU permissions must not disable memory, frames, or sandbox evidence.
            if (ticks.ExitCode != 0 || !long.TryParse(ticks.Text, out clockTicks) || clockTicks <= 0)
                clockTicks = 0;
            var package = (await AdbAsync("shell", "dumpsys package " + Quote(target.ApplicationIdentifier), cancellationToken).ConfigureAwait(false)).RequireText();
            Metadata["appVersion"] = Match(package, @"versionName=(\S+)");
            Metadata["buildNumber"] = Match(package, @"versionCode=(\d+)");
            Metadata["operatingSystemVersion"] = (await AdbAsync("shell", "getprop ro.build.version.release", cancellationToken).ConfigureAwait(false)).RequireText();
            Metadata["deviceConfiguration"] = (await AdbAsync("shell", "getprop ro.build.fingerprint; getconf _NPROCESSORS_ONLN; cat /proc/meminfo | head -n 1", cancellationToken).ConfigureAwait(false)).RequireText();
            androidRoot = Match(package, @"dataDir=(\S+)");
        }
        else
        {
            appBundlePath = await ContainerAsync("app", cancellationToken).ConfigureAwait(false);
            Metadata["appVersion"] = await PlistAsync("CFBundleShortVersionString", cancellationToken).ConfigureAwait(false);
            Metadata["buildNumber"] = await PlistAsync("CFBundleVersion", cancellationToken).ConfigureAwait(false);
            var devices = await commands.RunAsync("/usr/bin/xcrun", ["simctl", "list", "devices", "available", "--json"], cancellationToken).ConfigureAwait(false);
            var runtimes = JsonNode.Parse(devices.RequireText())?["devices"]?.AsObject();
            foreach (var runtime in runtimes ?? new JsonObject())
                if (runtime.Value is JsonArray entries && entries.OfType<JsonObject>().Any(device => device["udid"]?.GetValue<string>() == target.DeviceIdentifier))
                {
                    Metadata["operatingSystemVersion"] = runtime.Key.Split(".iOS-", StringSplitOptions.None).Last().Replace('-', '.');
                    var device = entries.OfType<JsonObject>().Single(device => device["udid"]?.GetValue<string>() == target.DeviceIdentifier);
                    Metadata["deviceConfiguration"] = runtime.Key + ":" + device["deviceTypeIdentifier"]?.GetValue<string>();
                }
        }
        if (OperatingSystem.IsMacOS())
        {
            var model = await commands.RunAsync("/usr/sbin/sysctl", ["-n", "hw.model", "machdep.cpu.brand_string", "hw.memsize"], cancellationToken).ConfigureAwait(false);
            Metadata["hostModel"] = model.ExitCode == 0 ? model.Text : "unknown";
        }
        await ProbeFilesAsync(cancellationToken).ConfigureAwait(false);
        // Icon discovery is optional and must never prevent telemetry or sandbox capture.
        try
        {
            AppIcon = await DeviceAppIconCapture.CaptureAsync(target, commands, resolvedAdb, cancellationToken).ConfigureAwait(false);
            Metadata["appIconProvider"] = AppIcon is null ? "unavailable" : IsAndroid ? "android-package-manager" : "ios-simulator-uikit";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Metadata["appIconProvider"] = "unavailable";
            Metadata["appIconUnavailableReason"] = exception.Message;
        }
    }

    public async Task<DeviceProcessSample?> SampleAsync(CancellationToken cancellationToken)
    {
        if (IsAndroid)
        {
            var result = await AdbAsync("shell", "pidof " + Quote(target.ApplicationIdentifier), cancellationToken).ConfigureAwait(false);
            var pids = result.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pids.Length != 1 || !int.TryParse(pids[0], out var pid)) return null;
            var stat = await AdbAsync("shell", $"cat /proc/{pid}/stat", cancellationToken).ConfigureAwait(false);
            var memory = await AdbAsync("shell", $"dumpsys meminfo {pid}", cancellationToken).ConfigureAwait(false);
            // Verify the PID again after sampling. A restart must never splice counters together.
            var after = await AdbAsync("shell", "pidof " + Quote(target.ApplicationIdentifier), cancellationToken).ConfigureAwait(false);
            if (after.Text != result.Text) return null;
            return DeviceProcessCounters.ParseAndroid(pid, stat.ExitCode == 0 ? stat.Text : "",
                memory.ExitCode == 0 ? memory.Text : "", clockTicks);
        }
        var services = await commands.RunAsync("/usr/bin/xcrun", ["simctl", "spawn", target.DeviceIdentifier,
            "launchctl", "list"], cancellationToken).ConfigureAwait(false);
        var matching = services.RequireText().Split('\n')
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[2].StartsWith("UIKitApplication:" + target.ApplicationIdentifier + "[", StringComparison.Ordinal))
            .ToArray();
        if (matching.Length != 1 || !int.TryParse(matching[0][0], out var simulatorPid)) return null;
        return DeviceProcessCounters.ReadMac(simulatorPid, appBundlePath ?? throw new IOException("App bundle is unresolved."));
    }

    public async Task<string> ReadFramesAsync(CancellationToken cancellationToken)
        => (await AdbAsync("shell", "dumpsys gfxinfo " + Quote(target.ApplicationIdentifier) + " framestats", cancellationToken).ConfigureAwait(false)).RequireText();

    public async Task ProbeFilesAsync(CancellationToken cancellationToken)
    {
        FileProvider = null;
        try
        {
            if (!IsAndroid)
            {
                var root = await ContainerAsync("data", cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(root)) throw new IOException("The simulator data container is unavailable.");
                FileProvider = "simctl-container";
            }
            else
            {
                var user = (await AdbAsync("shell", "am get-current-user", cancellationToken).ConfigureAwait(false)).RequireText();
                if (!int.TryParse(user, out var userId) || userId < 0) throw new IOException("The Android user could not be resolved.");
                var prefix = "run-as " + Quote(target.ApplicationIdentifier) + " --user " + userId.ToString(CultureInfo.InvariantCulture);
                var probe = await AdbAsync("exec-out", prefix + " pwd", cancellationToken).ConfigureAwait(false);
                if (probe.ExitCode == 0 && probe.Text.StartsWith("/", StringComparison.Ordinal))
                {
                    androidFilePrefix = prefix;
                    androidRoot = probe.Text;
                    FileProvider = "adb-run-as";
                }
                else
                {
                    var uid = await AdbAsync("shell", "id -u", cancellationToken).ConfigureAwait(false);
                    if (uid.ExitCode != 0 || uid.Text != "0" || string.IsNullOrWhiteSpace(androidRoot))
                        throw new IOException("Private files require a debuggable app (run-as) or an already provisioned root ADB daemon.");
                    // Resolve the current Android user's data root from the package's canonical /data/user layout.
                    androidRoot = $"/data/user/{userId}/{target.ApplicationIdentifier}";
                    var rootProbe = await AdbAsync("shell", "test -d " + Quote(androidRoot), cancellationToken).ConfigureAwait(false);
                    rootProbe.RequireText();
                    androidFilePrefix = "";
                    FileProvider = "adb-root";
                }
            }
            FileUnavailableReason = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileUnavailableReason = exception.Message;
        }
    }

    public async Task<byte[]> ReadFileAsync(string root, string path, int limit, CancellationToken cancellationToken)
    {
        ValidateRelativePath(path);
        if (IsAndroid)
        {
            var result = await SandboxCommandAsync(root, path, "test -f \"$p\" && head -c " + (limit + 1) + " \"$p\"", cancellationToken, limit + 1).ConfigureAwait(false);
            result.RequireText();
            if (result.Output.Length > limit) throw new IOException($"File exceeds the {limit}-byte limit.");
            return result.Output;
        }
        var absolute = ResolveContainedPath(await ContainerAsync(root, cancellationToken).ConfigureAwait(false), path);
        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new IOException($"File exceeds the {limit}-byte limit.");
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > limit) throw new IOException($"File exceeds the {limit}-byte limit.");
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    public async Task<string[]> ListFilesAsync(string root, string path, CancellationToken cancellationToken)
    {
        ValidateRelativePath(path, allowEmpty: true);
        if (IsAndroid)
        {
            var result = await SandboxCommandAsync(root, path, "test -d \"$p\" && find \"$p\" -mindepth 1 -maxdepth 1 -print0", cancellationToken, 262_144).ConfigureAwait(false);
            result.RequireText();
            return Encoding.UTF8.GetString(result.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName).OfType<string>().Take(1000).ToArray();
        }
        var absolute = ResolveContainedPath(await ContainerAsync(root, cancellationToken).ConfigureAwait(false), path);
        return Directory.EnumerateFileSystemEntries(absolute).Take(1000).Select(Path.GetFileName).OfType<string>().ToArray();
    }

    private async Task<DeviceCommandResult> SandboxCommandAsync(string root, string path, string action, CancellationToken cancellationToken, int limit)
    {
        if (root != "data") throw new IOException("Android external file access currently supports the data root only.");
        // Reprobe permissions on every request; a reinstall can change debuggability or the data root.
        await ProbeFilesAsync(cancellationToken).ConfigureAwait(false);
        if (FileProvider is null) throw new IOException(FileUnavailableReason);
        var script = "r=$(realpath " + Quote(androidRoot!) + ") && p=$(realpath \"$r\"/" + Quote(path.Length == 0 ? "." : path)
            + ") && case \"$p\" in \"$r\"|\"$r\"/*) " + action + ";; *) exit 64;; esac";
        return await AdbAsync("exec-out", androidFilePrefix + " sh -c " + Quote(script), cancellationToken, limit).ConfigureAwait(false);
    }

    private async Task<string> ContainerAsync(string root, CancellationToken cancellationToken)
    {
        var selector = root is "data" or "app" ? root
            : root.StartsWith("group:", StringComparison.Ordinal) && root.Length > 6 ? root[6..]
            : throw new ArgumentException("Root must be data, app, or group:<identifier>.");
        var result = await commands.RunAsync("/usr/bin/xcrun", ["simctl", "get_app_container", target.DeviceIdentifier,
            target.ApplicationIdentifier, selector], cancellationToken).ConfigureAwait(false);
        var container = result.RequireText();
        if (!Path.IsPathFullyQualified(container) || !Directory.Exists(container)) throw new IOException("The simulator container could not be resolved.");
        return container;
    }

    private async Task<string?> PlistAsync(string key, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", Path.Combine(appBundlePath!, "Info.plist")], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Text : null;
    }

    private Task<DeviceCommandResult> AdbAsync(string mode, string command, CancellationToken cancellationToken, int limit = 1_048_576)
        => commands.RunAsync(resolvedAdb ?? throw new IOException("ADB is unavailable."), ["-s", target.DeviceIdentifier, mode, command], cancellationToken, limit);

    internal static void ValidateRelativePath(string path, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(path)) || Path.IsPathRooted(path) || path.Contains('\\')
            || path.Any(char.IsControl) || path.Split('/').Any(part => part is ".." or "."))
            throw new ArgumentException("A root-relative path without traversal or control characters is required.");
    }

    internal static string ResolveContainedPath(string root, string relative)
    {
        ValidateRelativePath(relative, allowEmpty: true);
        var current = Path.GetFullPath(root);
        foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("External file reads do not follow sandbox symlinks.");
        }
        return current;
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string Quote(string value) => DeviceCommandRunner.Quote(value);
}
