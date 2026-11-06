using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Ansight.Adb;
using Ansight.SimCtl;

namespace Ansight.Host.Diagnostics;

internal sealed class SessionHostMetadataCollector : ISessionHostMetadataCollector
{
    private readonly RuntimeOptions options;

    public SessionHostMetadataCollector(RuntimeOptions options)
    {
        this.options = options;
    }

    internal static SessionHostMetadata CreateSnapshot(string? platform = null)
    {
        var assembly = Assembly.GetEntryAssembly();
        var isCli = string.Equals(assembly?.GetName().Name, "ansight", StringComparison.OrdinalIgnoreCase);
        var attributes = assembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        return new SessionHostMetadata
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            TargetPlatform = NormalizePlatform(platform),
            HostApplication = assembly?.GetName().Name,
            CliVersion = isCli ? HostHealthService.ResolveAssemblyProductVersion(assembly!) : null,
            CliInformationalVersion = isCli ? assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion : null,
            CliBuildNumber = isCli ? attributes?.GetValueOrDefault("AnsightCliBuildNumber") : null,
            CommitSha = isCli ? attributes?.GetValueOrDefault("AnsightCommitSha") : null,
            HostBuildId = typeof(SessionHostMetadataCollector).Module.ModuleVersionId.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            OsType = OperatingSystem.IsMacOS() ? "macos"
                : OperatingSystem.IsWindows() ? "windows"
                    : OperatingSystem.IsLinux() ? "linux" : "other",
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            DotNetRuntime = RuntimeInformation.FrameworkDescription
        };
    }

    internal static string? NormalizePlatform(string? platform)
    {
        return platform?.Trim().ToLowerInvariant() switch
        {
            "ios" or "ipados" => "ios",
            "android" => "android",
            null or "" => null,
            var value => value
        };
    }

    public async Task<SessionHostMetadata> CollectAsync(SessionHostMetadata snapshot)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.FeatureLifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        var token = timeout.Token;
        var probes = new List<Task<SessionHostToolMetadata>>
        {
            GuardAsync("node", () => InspectAsync("node", options.JavaScriptExecutablePath, ["--version"], token)),
            GuardAsync("tesseract", () => InspectOcrAsync(token)),
            GuardAsync("ffmpeg", () => InspectAsync("ffmpeg", null, ["-version"], token)),
            GuardAsync("mitmdump", () => InspectAsync("mitmdump", Environment.GetEnvironmentVariable("ANSIGHT_MITMDUMP_PATH"), ["--version"], token)),
            GuardAsync("dotnet-trace", () => InspectAsync("dotnet-trace", null, ["--version"], token)),
            GuardAsync("dotnet-dsrouter", () => InspectAsync("dotnet-dsrouter", null, ["--version"], token))
        };

        if (snapshot.TargetPlatform == "ios" && OperatingSystem.IsMacOS())
        {
            probes.Add(GuardAsync("xcode", () => InspectXcodeAsync(token)));
            probes.Add(GuardAsync("core-simulator", () => InspectPlistAsync("core-simulator",
                "/Library/Developer/PrivateFrameworks/CoreSimulator.framework/Resources/Info.plist", token)));
            probes.Add(GuardAsync("appium", () => InspectAsync("appium", null, ["--version"], token)));
            probes.Add(GuardAsync("axe", () => InspectAxeAsync(token)));
        }

        if (snapshot.TargetPlatform == "android")
        {
            var adb = AdbToolLocator.Resolve(options.AdbPath);
            var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : options.AdbPath);
            probes.Add(GuardAsync("adb", async () => adb.IsFound
                ? (await InspectAsync("adb", adb.AdbPath, ["version"], token)) with
                {
                    SelectedVia = adb.Source
                } : new SessionHostToolMetadata("adb", "missing")));
            probes.Add(GuardAsync("android-emulator", async () => emulator.IsFound
                ? (await InspectAsync("android-emulator", emulator.EmulatorPath, ["-version"], token)) with
                {
                    SelectedVia = emulator.Source
                } : new SessionHostToolMetadata("android-emulator", "missing")));
            probes.Add(GuardAsync("scrcpy", () => InspectScrcpyAsync(token)));
            probes.Add(GuardAsync("java", () => InspectAsync("java", null, ["-version"], token)));
            probes.Add(GuardAsync("android-studio", () => Task.FromResult(InspectAndroidStudio())));
        }

        var os = await ReadOperatingSystemAsync(snapshot, token).ConfigureAwait(false);
        var tools = await Task.WhenAll(probes).ConfigureAwait(false);
        var partial = tools.Any(tool => tool.Status is "failed" or "timed-out" or "not-checked")
            || os.Status == "partial";

        return os with
        {
            Status = partial ? "partial" : "complete",
            CollectionCompletedUtc = DateTimeOffset.UtcNow,
            RepositoryAutomationsEnabled = options.EnableRepositoryAutomations,
            Tools = Array.AsReadOnly(tools)
        };
    }

    private static async Task<SessionHostMetadata> ReadOperatingSystemAsync(SessionHostMetadata snapshot, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return snapshot;
        }

        var version = await GuardAsync("macos", async () =>
        {
            var result = await DiagnosticProcess.RunAsync("/usr/bin/sw_vers", ["-productVersion"], token);

            return new SessionHostToolMetadata("macos", result.Status,
                result.Status == "available" ? result.Output : null);
        });
        var build = await GuardAsync("macos-build", async () =>
        {
            var result = await DiagnosticProcess.RunAsync("/usr/bin/sw_vers", ["-buildVersion"], token);

            return new SessionHostToolMetadata("macos-build", result.Status,
                result.Status == "available" ? result.Output : null);
        });

        return snapshot with
        {
            OsVersion = version.Version ?? snapshot.OsVersion,
            OsBuild = build.Version,
            Status = version.Status == "available" && build.Status == "available" ? "collecting" : "partial"
        };
    }

    private static async Task<SessionHostToolMetadata> GuardAsync(string name, Func<Task<SessionHostToolMetadata>> probe)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SessionHostToolMetadata(name, "timed-out");
        }
        catch (Exception)
        {
            // Version discovery is optional. Never retain raw command errors or environment values.
            return new SessionHostToolMetadata(name, "not-checked");
        }
    }

    private static async Task<SessionHostToolMetadata> InspectAsync(
        string name, string? path, string[] arguments, CancellationToken token)
    {
        var tool = await DiagnosticProcess.InspectAsync(name, path, "Capture tooling", token, arguments);
        var status = tool.Status == "available" && tool.Version is null ? "not-checked" : tool.Status;

        return new SessionHostToolMetadata(name, status, tool.Version, Path: tool.Path, SelectedVia: tool.SelectedVia);
    }

    private static async Task<SessionHostToolMetadata> InspectOcrAsync(CancellationToken token)
    {
        var path = TesseractSessionScreenshotOcrScanner.ResolveExecutablePath(null);

        if (path is null)
        {
            return new SessionHostToolMetadata("tesseract", "missing");
        }

        var tool = await InspectAsync("tesseract", path, ["--version"], token);
        var selectedVia = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_TESSERACT_PATH"))
            ? "PATH" : "ANSIGHT_TESSERACT_PATH";

        return tool with
        {
            SelectedVia = selectedVia
        };
    }

    private async Task<SessionHostToolMetadata> InspectXcodeAsync(CancellationToken token)
    {
        var xcode = await SimCtlToolLocator.ResolveAsync(options.XcodePath, token);

        if (!xcode.IsFound)
        {
            return new SessionHostToolMetadata("xcode", "missing");
        }

        var environment = new Dictionary<string, string> { ["DEVELOPER_DIR"] = xcode.DeveloperDirectory };
        var result = await DiagnosticProcess.RunAsync(xcode.XcrunPath, ["xcodebuild", "-version"], token, environment);
        var version = result.Status == "available" ? DiagnosticProcess.ParseVersion("xcode", result.Output) : null;
        var build = result.Status == "available"
            ? result.Output?.Split('\n').FirstOrDefault(line => line.StartsWith("Build version ", StringComparison.Ordinal))?[14..].Trim()
            : null;

        return new SessionHostToolMetadata("xcode", result.Status, version, build,
            xcode.DeveloperDirectory, xcode.Source);
    }

    private async Task<SessionHostToolMetadata> InspectAxeAsync(CancellationToken token)
    {
        var path = IosSimulatorAccessibilityClient.ResolveExecutablePath(options.IosSimulatorAxePath);

        if (path is null)
        {
            return new SessionHostToolMetadata("axe", "missing");
        }

        var tool = await InspectAsync("axe", path, ["--version"], token);
        var selectedVia = !string.IsNullOrWhiteSpace(options.IosSimulatorAxePath) ? "configured"
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_AXE_PATH")) ? "ANSIGHT_AXE_PATH"
                : "discovery";

        return tool with
        {
            SelectedVia = selectedVia
        };
    }

    private static async Task<SessionHostToolMetadata> InspectPlistAsync(string name, string path, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            return new SessionHostToolMetadata(name, "missing");
        }

        var result = await DiagnosticProcess.RunAsync("/usr/bin/plutil", ["-extract", "CFBundleVersion", "raw", "-o", "-", path], token);

        return new SessionHostToolMetadata(name, result.Status,
            result.Status == "available" ? result.Output : null, Path: path);
    }

    private static async Task<SessionHostToolMetadata> InspectScrcpyAsync(CancellationToken token)
    {
        var tool = await ScrcpyToolLocator.ResolveAsync(cancellationToken: token);

        return tool.IsFound
            ? new SessionHostToolMetadata("scrcpy", "available", tool.Version, Path: tool.ExecutablePath, SelectedVia: tool.Source)
            : new SessionHostToolMetadata("scrcpy", "unavailable");
    }

    internal static SessionHostToolMetadata ReadAndroidStudioProduct(string path)
    {
        var product = JsonNode.Parse(File.ReadAllText(path));
        var version = product?["version"]?.GetValue<string>();
        var build = product?["buildNumber"]?.GetValue<string>();

        return new SessionHostToolMetadata("android-studio", string.IsNullOrWhiteSpace(version) ? "not-checked" : "available",
            version, build, path, "installation");
    }

    private static SessionHostToolMetadata InspectAndroidStudio()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_STUDIO_HOME"),
            "/Applications/Android Studio.app",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Android Studio.app"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio"),
            "/opt/android-studio",
            "/usr/local/android-studio"
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var relative in new[] { "product-info.json", "Contents/Resources/product-info.json" })
            {
                var path = Path.Combine(root!, relative);

                if (File.Exists(path))
                {
                    return ReadAndroidStudioProduct(path);
                }
            }
        }

        return new SessionHostToolMetadata("android-studio", "not-found");
    }
}
