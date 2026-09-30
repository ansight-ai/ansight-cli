using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Ansight.Adb;
using Ansight.SimCtl;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Diagnostics;

public sealed partial class SystemReportService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly RuntimeOptions options;
    private readonly RuntimeCoordinator? runtime;
    private readonly string dataDirectory;

    public SystemReportService(RuntimeOptions options, RuntimeCoordinator? runtime = null)
    {
        this.options = options;
        this.runtime = runtime;
        dataDirectory = Path.GetFullPath(options.BaseFolderPath ?? runtime?.BaseFolderPath ?? ".");
    }

    public async Task<SystemReport> CollectAsync(bool includeSecretMetadata = false, CancellationToken cancellationToken = default)
    {
        var sections = new Dictionary<string, SystemReportSection>();
        var findings = new List<string>();
        var collectors = new Dictionary<string, Func<CancellationToken, Task<SystemReportSection>>>
        {
            ["machine"] = token => CollectMachineAsync(token),
            ["installation"] = token => Task.FromResult(CollectInstallation()),
            ["host"] = token => Task.FromResult(CollectHost()),
            ["configuration"] = token => Task.FromResult(CollectConfiguration()),
            ["dependencies"] = token => CollectDependenciesAsync(token),
            ["platformTooling"] = token => CollectPlatformAsync(token),
            ["apps"] = token => Task.FromResult(CollectApps(token)),
            ["devices"] = token => CollectDevicesAsync(token),
            ["agents"] = token => CollectAgentsAsync(token),
            ["skills"] = token => Task.FromResult(CollectSkills(token))
        };
        // Probe failures are isolated; never include exception text that may contain credentials.
        var results = await Task.WhenAll(collectors.Select(async collector =>
            new KeyValuePair<string, SystemReportSection>(collector.Key,
                await GuardAsync(collector.Value, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        foreach (var result in results) sections.Add(result.Key, result.Value);
        cancellationToken.ThrowIfCancellationRequested();
        sections["secretMetadata"] = CollectSecretMetadata(includeSecretMetadata, ReadSecretMetadata);
        foreach (var section in sections.Where(section => section.Value.Status is "partial" or "not-checked" or "timed-out"))
            findings.Add($"{section.Key}: {section.Value.Message ?? "Some inventory could not be inspected."}");
        var host = sections["host"].Data;
        var installation = sections["installation"].Data;
        if (host?["isRunning"]?.GetValue<bool>() == true && host?["cliBuildNumber"] is not null && installation?["buildNumber"] is not null
            && host["cliBuildNumber"]!.ToString() != installation["buildNumber"]!.ToString())
            findings.Add("The running host and this CLI have different builds. Restart the host to load the installed build.");
        if (sections["skills"].Data is JsonArray skills)
        {
            foreach (var skill in skills.Where(skill => skill?["comparison"]?.ToString() is "differs-from-bundled" or "incomplete"))
                findings.Add($"Skill {skill!["id"]}: {skill["comparison"]}.");
            foreach (var group in skills.GroupBy(skill => skill?["id"]?.ToString()).Where(group => group.Count() > 1))
                findings.Add($"Skill {group.Key} has multiple installations; check the agent's project and user skill selection.");
        }
        if (sections["dependencies"].Data is JsonArray dependencies)
            foreach (var dependency in dependencies.Where(item => item?["status"]?.ToString() != "available"))
                findings.Add($"{dependency!["name"]}: {dependency["status"]}. Required by: {dependency["usedBy"]}.");
        return new("ansight.system-report/v1", DateTimeOffset.UtcNow, includeSecretMetadata, sections, findings);
    }

    internal static SystemReportSection CollectSecretMetadata(bool include, Func<JsonNode> collector)
    {
        if (!include) return new("not-requested");
        try { return new("complete", collector()); }
        catch { return new("not-checked", Message: "Secret metadata could not be read from the configured provider."); }
    }

    private static async Task<SystemReportSection> GuardAsync(Func<CancellationToken, Task<SystemReportSection>> collector, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try { return await collector(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("timed-out"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new("not-checked", Message: $"The inventory probe failed ({exception.GetType().Name})."); }
    }

    private async Task<SystemReportSection> CollectMachineAsync(CancellationToken token)
    {
        long? memory = null;
        string? cpu = null;
        var osVersion = Environment.OSVersion.VersionString;
        string? osBuild = null;
        if (OperatingSystem.IsMacOS())
        {
            var memoryProbe = await DiagnosticProcess.RunAsync("/usr/sbin/sysctl", ["-n", "hw.memsize"], token);
            if (long.TryParse(memoryProbe.Output, out var bytes)) memory = bytes;
            cpu = (await DiagnosticProcess.RunAsync("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"], token)).Output;
            var versionProbe = await DiagnosticProcess.RunAsync("/usr/bin/sw_vers", ["-productVersion"], token);
            if (versionProbe.Status == "available") osVersion = versionProbe.Output!;
            var buildProbe = await DiagnosticProcess.RunAsync("/usr/bin/sw_vers", ["-buildVersion"], token);
            if (buildProbe.Status == "available") osBuild = buildProbe.Output;
        }
        else if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var line = File.ReadLines("/proc/meminfo").FirstOrDefault(value => value.StartsWith("MemTotal:"));
            if (long.TryParse(line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var kb)) memory = kb * 1024;
            cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(value => value.StartsWith("model name"))?.Split(':', 2).Last().Trim();
        }
        else if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status)) memory = (long)status.totalPhysical;
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }
        var disks = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new
        { path = drive.Name, format = drive.DriveFormat, capacityBytes = drive.TotalSize, freeBytes = drive.AvailableFreeSpace }).ToArray();
        return Complete(new { operatingSystem = RuntimeInformation.OSDescription, osVersion, osBuild,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount, processor = cpu, totalMemoryBytes = memory, disks,
            dotNetRuntime = RuntimeInformation.FrameworkDescription });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint length; public uint load; public ulong totalPhysical; public ulong availablePhysical;
        public ulong totalPageFile; public ulong availablePageFile; public ulong totalVirtual; public ulong availableVirtual; public ulong availableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private SystemReportSection CollectInstallation()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SystemReportService).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        var receiptPath = FindReceiptPath();
        var receipt = File.Exists(receiptPath) ? ReadObject(receiptPath) : null;
        return Complete(new { version = HostHealthService.ResolveAssemblyProductVersion(assembly),
            buildNumber = metadata.GetValueOrDefault("AnsightCliBuildNumber"), commit = metadata.GetValueOrDefault("AnsightCommitSha"),
            executablePath = Environment.ProcessPath, runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            receiptPath, receipt = receipt is null ? null : Pick(receipt, "version", "buildNumber", "channel", "rid", "installedAtUtc", "installRoot", "binDirectory", "sha256"),
            bundledLibraries = ReadBundledLibraries() });
    }

    internal static string FindReceiptPath()
    {
        var configured = Environment.GetEnvironmentVariable("ANSIGHT_INSTALL_RECEIPT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        if (directory.Parent?.Name == "versions") return Path.Combine(directory.Parent.Parent!.FullName, "install.json");
        var root = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ansight", "cli-install")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ansight", "cli");
        return Path.Combine(root, "install.json");
    }

    private static JsonNode ReadBundledLibraries()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "ansight.deps.json");
        if (!File.Exists(file)) return new JsonArray();
        var libraries = ReadObject(file)["libraries"] as JsonObject;
        return Node(libraries?.Select(item => new { identity = item.Key, type = item.Value?["type"]?.ToString() }).ToArray() ?? []);
    }

    private SystemReportSection CollectHost()
    {
        var path = Path.Combine(dataDirectory, "ansight-host.json");
        if (!File.Exists(path)) return Complete(new { isRunning = false, metadataStatus = "missing", dataDirectory });
        var metadata = ReadObject(path);
        var selected = Pick(metadata, "processId", "processStartedUtc", "updatedUtc", "cliVersion", "cliBuildNumber", "commitSha", "logFilePath", "dataDirectory");
        var running = false;
        try
        {
            var pid = selected["processId"]?.GetValue<int>() ?? 0;
            using var process = Process.GetProcessById(pid);
            running = !process.HasExited;
            var started = Get(metadata, "ProcessStartUtc") ?? Get(metadata, "ProcessStartedUtc");
            if (started is not null && DateTimeOffset.TryParse(started.ToString(), out var expected))
                running &= Math.Abs((process.StartTime.ToUniversalTime() - expected.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        selected["isRunning"] = running;
        selected["metadataStatus"] = running ? "current" : "stale";
        // Explorer URLs contain an access path; only disclose their origin.
        var explorer = Get(metadata, "ExplorerUrl")?.ToString();
        selected["explorerOrigin"] = Uri.TryCreate(explorer, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : null;
        return new("complete", selected);
    }

    private SystemReportSection CollectConfiguration() => Complete(new
    {
        dataDirectory, logDirectory = Path.Combine(dataDirectory, "logs"),
        adbPath = options.AdbPath, xcodePath = options.XcodePath, nodePath = options.JavaScriptExecutablePath,
        discoveryPort = options.DiscoveryPort, webSocketPort = options.WebSocketPort,
        repositoryAutomationsEnabled = options.EnableRepositoryAutomations,
        automationRepositoryPaths = options.AutomationRepositoryPaths,
        credentialProvider = EncryptedStorageFactory.ResolveDefaultProviderName(options.SecureStorageFilePath, options.SecureStorageKeyFilePath),
        environmentContext = runtime is null ? "cli-process" : "resident-host",
        hostEnvironmentComparison = runtime is null ? "not-checked: use the local host doctor API to inspect the resident host environment" : "resident host selected"
    });

    private async Task<SystemReportSection> CollectDependenciesAsync(CancellationToken token)
    {
        var adb = AdbToolLocator.Resolve(options.AdbPath);
        var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : options.AdbPath);
        var definitions = new[]
        {
            new ToolDefinition("node", options.JavaScriptExecutablePath, "Workspace tasks, triggers and sanitizers", ["--version"]),
            new ToolDefinition("tesseract", Environment.GetEnvironmentVariable("ANSIGHT_TESSERACT_PATH"), "Screenshot OCR", ["--version"]),
            new ToolDefinition("scrcpy", null, "Android video", ["--version"]),
            new ToolDefinition("dotnet-trace", null, ".NET profiling", ["--version"]),
            new ToolDefinition("dotnet-dsrouter", null, ".NET diagnostics routing", ["--version"]),
            new ToolDefinition("dotnet", null, "Optional SDK and diagnostic-tool installation", ["--version"]),
            new ToolDefinition("appium", null, "Physical iOS input", ["--version"]),
            new ToolDefinition("adb", adb.IsFound ? adb.AdbPath : options.AdbPath, "Android devices", ["version"]),
            new ToolDefinition("emulator", emulator.IsFound ? emulator.EmulatorPath : null, "Android emulators", ["-version"])
        };
        var tools = await Task.WhenAll(definitions.Select(tool => DiagnosticProcess.InspectAsync(tool.Name, tool.Path, tool.UsedBy, token, tool.Arguments)));
        tools = tools.Select(tool => tool.Name switch
        {
            "adb" when adb.IsFound => tool with { SelectedVia = adb.Source },
            "emulator" when emulator.IsFound => tool with { SelectedVia = emulator.Source },
            _ => tool
        }).ToArray();
        return new(tools.Any(tool => tool.Status is "timed-out" or "not-checked" or "failed") ? "partial" : "complete", Node(tools));
    }

    private sealed record ToolDefinition(string Name, string? Path, string UsedBy, string[] Arguments);

    private async Task<SystemReportSection> CollectPlatformAsync(CancellationToken token)
    {
        var probes = new Dictionary<string, DiagnosticProcessResult>();
        var dotnet = DiagnosticProcess.FindExecutables("dotnet").FirstOrDefault();
        if (dotnet is not null)
        {
            probes["dotnetSdks"] = await DiagnosticProcess.RunAsync(dotnet, ["--list-sdks"], token);
            probes["dotnetRuntimes"] = await DiagnosticProcess.RunAsync(dotnet, ["--list-runtimes"], token);
        }
        string? developerDirectory = null;
        if (OperatingSystem.IsMacOS())
        {
            var xcode = await SimCtlToolLocator.ResolveAsync(options.XcodePath, token);
            if (xcode.IsFound)
            {
                developerDirectory = xcode.DeveloperDirectory;
                var environment = new Dictionary<string, string> { ["DEVELOPER_DIR"] = developerDirectory };
                probes["xcode"] = await DiagnosticProcess.RunAsync(xcode.XcrunPath, ["xcodebuild", "-version"], token, environment);
                probes["simulatorRuntimes"] = await DiagnosticProcess.RunAsync(xcode.XcrunPath, ["simctl", "list", "runtimes", "--json"], token, environment);
            }
        }
        var appium = DiagnosticProcess.FindExecutables("appium").FirstOrDefault();
        if (appium is not null) probes["appiumDrivers"] = await DiagnosticProcess.RunAsync(appium, ["driver", "list", "--installed", "--json"], token);
        var adb = AdbToolLocator.Resolve(options.AdbPath);
        var sdkRoot = adb.IsFound ? Directory.GetParent(adb.AdbPath)?.Parent?.FullName : null;
        var packages = new List<object>();
        if (sdkRoot is not null && Directory.Exists(sdkRoot))
        {
            foreach (var file in EnumerateFiles(sdkRoot, "source.properties", 4, 500))
            {
                var properties = File.ReadAllLines(file).Where(line => line.StartsWith("Pkg.Revision=") || line.StartsWith("Pkg.Desc="));
                packages.Add(new { path = Path.GetDirectoryName(file), properties = properties.ToArray() });
            }
        }
        var emulator = AndroidEmulatorToolLocator.Resolve(adb.IsFound ? adb.AdbPath : options.AdbPath);
        if (emulator.IsFound) probes["androidAvds"] = await DiagnosticProcess.RunAsync(emulator.EmulatorPath, ["-list-avds"], token);
        return new(probes.Values.Any(probe => probe.Status != "available") ? "partial" : "complete",
            Node(new { developerDirectory, androidSdkRoot = sdkRoot, androidPackages = packages, probes }));
    }

    private async Task<SystemReportSection> CollectDevicesAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var service = runtime?.Devices ?? new DeviceService(options);
        var inventory = await service.ListAsync(timeout.Token).ConfigureAwait(false);
        var apps = new List<object>();
        var partial = inventory.Warnings.Count > 0;
        foreach (var device in inventory.Devices)
        {
            if (!device.IsAvailable || (!device.IsBooted && (device.Platform != "ios" || device.IsPhysical)))
            {
                apps.Add(new { deviceId = device.Identifier, status = "not-checked", reason = "Target is offline." });
                partial = true;
                continue;
            }
            using var deviceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            deviceTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var installed = await service.ListApplicationsAsync(device.Platform, device.Identifier, deviceTimeout.Token);
                apps.Add(new { deviceId = device.Identifier, status = "complete", applications = installed });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { apps.Add(new { deviceId = device.Identifier, status = "not-checked" }); partial = true; }
        }
        return new(partial ? "partial" : "complete", Node(new { inventory.Capabilities, inventory.Devices, installedApplications = apps }),
            partial ? "Some device inventories could not be inspected; no devices were started." : null);
    }

    private SystemReportSection CollectApps(CancellationToken token)
    {
        var definitions = ReadAppDefinitions();
        var sessions = new JsonArray();
        foreach (var path in EnumerateFiles(Path.Combine(dataDirectory, "data", "session-captures"), "session.json", 2, 2000))
        {
            token.ThrowIfCancellationRequested();
            var summary = ReadObject(path);
            var selected = Pick(summary, "sessionId", "appId", "clientName", "createdUtc", "lastUpdatedUtc", "sdkVersion");
            var profile = Path.Combine(Path.GetDirectoryName(path)!, "device-profile.json");
            if (File.Exists(profile)) selected["profile"] = SelectObservedProfile(ReadObject(profile));
            sessions.Add(selected);
        }
        return Complete(new { registeredApps = definitions, observedSessions = sessions,
            liveCatalog = runtime?.Apps.List().Select(app => new { app.AppId, app.Name, app.SessionCount, app.LiveSessionCount, app.CodebasePath }),
            scope = "Registered apps and up to 2000 retained session summaries; archived ZIP contents are not expanded." });
    }

    internal static JsonObject SelectObservedProfile(JsonObject document)
    {
        var encoded = Get(document, "profileJson")?.GetValue<string>();
        var profile = encoded is null ? document : JsonNode.Parse(encoded)?.AsObject() ?? new JsonObject();
        var selected = new JsonObject();
        if (Get(profile, "app") is JsonObject app)
            selected["app"] = Pick(app, "appId", "appName", "versionName", "versionCode", "buildNumber", "installSource", "firstInstallTimeMs", "lastUpdateTimeMs");
        if (Get(profile, "sdk") is JsonObject sdk)
            selected["sdk"] = Pick(sdk, "name", "packageId", "version", "language");
        if (Get(profile, "device") is JsonObject device)
            selected["device"] = Pick(device, "model", "osName", "osVersion", "osBuild", "cpuArch", "isVirtual", "isEmulator");
        if (Get(profile, "runtime") is JsonObject runtimeProfile)
            selected["runtime"] = Pick(runtimeProfile, "primary", "primaryVersion", "aotEnabled", "jitEnabled");
        return selected;
    }

    private IReadOnlyList<KnownAppDefinition> ReadAppDefinitions()
    {
        if (runtime is not null) return runtime.Apps.GetDefinitions();
        var path = Path.Combine(dataDirectory, "data", "known-apps.json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<KnownAppDocument>(File.ReadAllText(path), JsonOptions)?.Apps ?? [];
    }

    private JsonNode ReadSecretMetadata()
    {
        var definitions = ReadAppDefinitions();
        var storage = EncryptedStorageFactory.CreateReadOnly(Path.Combine(dataDirectory, "data", "secure-storage.json"), options.SecureStorageFilePath, options.SecureStorageKeyFilePath);
        var store = new SecretStore(storage);
        var tests = definitions.Where(app => !string.IsNullOrWhiteSpace(app.CodebasePath))
            .Select(app => WorkspaceTestCatalog.Load(app.CodebasePath!)).ToArray();
        if (tests.Any(catalog => catalog.Warnings.Count > 0)) throw new InvalidDataException("Some secret declarations could not be read.");
        var declarations = tests.SelectMany(catalog => catalog.Tests).ToArray();
        var entries = new List<object>();
        foreach (var appId in definitions.Select(app => app.AppId).Concat(declarations.Select(test => test.AppId)).Distinct())
        {
            var stored = store.ListForDiagnostics(appId);
            foreach (var alias in stored.Select(secret => secret.Alias).Concat(declarations.Where(test => test.AppId == appId).SelectMany(test => test.RequiredSecrets)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var metadata = stored.FirstOrDefault(secret => string.Equals(secret.Alias, alias, StringComparison.OrdinalIgnoreCase));
                var environmentPresent = metadata is null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(alias));
                entries.Add(new { alias, appId, source = metadata is not null ? "configured-secret-store" : environmentPresent ? "environment" : "unresolved",
                    versionId = metadata?.VersionId, updatedUtc = metadata?.UpdatedUtc,
                    status = metadata is not null ? "indexed-not-validated" : environmentPresent ? "configured-not-validated" : "missing",
                    requiredBy = declarations.Where(test => test.AppId == appId && test.RequiredSecrets.Contains(alias, StringComparer.OrdinalIgnoreCase)).Select(test => test.TestId).ToArray() });
            }
        }
        return Node(entries);
    }

    internal static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, JsonOptions)!;
    private static SystemReportSection Complete(object value) => new("complete", Node(value));
    internal static JsonObject ReadObject(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Inventory file is too large.");
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
    }
    private static JsonNode? Get(JsonObject source, string name) => source.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    private static JsonObject Pick(JsonObject source, params string[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields) result[field] = Get(source, field)?.DeepClone();
        return result;
    }
    internal static IEnumerable<string> EnumerateFiles(string root, string pattern, int depth, int limit)
    {
        if (!Directory.Exists(root) || depth < 0) yield break;
        var pending = new Queue<DirectoryScan>(); pending.Enqueue(new(root, depth));
        var count = 0; var visited = 0;
        while (pending.TryDequeue(out var item))
        {
            if (++visited > 5000) throw new InvalidDataException("Inventory directory limit reached.");
            foreach (var file in Directory.EnumerateFiles(item.Path, pattern))
            {
                if (++count > limit) throw new InvalidDataException("Inventory limit reached.");
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            }
            if (item.Depth > 0)
                foreach (var directory in Directory.EnumerateDirectories(item.Path))
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Enqueue(new(directory, item.Depth - 1));
        }
    }
    private sealed record DirectoryScan(string Path, int Depth);
}
