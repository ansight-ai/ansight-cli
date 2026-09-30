using System.Text.RegularExpressions;

namespace Ansight.Cli.Commands.Setup;

internal static class SetupCommands
{
    public static async Task<int> RunAsync(CliArguments arguments, CliOutput output,
        TextReader? input, bool interactive, CancellationToken token)
    {
        if (arguments.Positionals.Count == 1 || CliCommandHelp.IsRequested(arguments))
            return CliCommandHelp.Write(output, Help);
        arguments.EnsurePositionalCount(2, "ansight setup android|credentials [options]");
        var target = arguments.RequirePositional(1, "setup target");
        if (target == "credentials")
        {
            var original = arguments.OriginalArguments.ToArray();
            original[arguments.GetOriginalArgumentIndexForPositional(0)] = "config";
            return ConfigCommands.Run(CliArguments.Parse(original), output, input, interactive);
        }
        if (target != "android") throw new CliUsageException("Setup supports android or credentials.");
        foreach (var flag in new[] { "yes", "dry-run", "accept-android-licenses" })
            if (arguments.GetOption(flag) is not null)
                throw new CliUsageException($"--{flag} takes no value; omit it instead of passing false.");

        var options = CliRuntime.ResolveOptions(arguments);
        var plan = AndroidSetupPlan.Discover(arguments, options.AdbPath);
        var dryRun = arguments.HasFlag("dry-run");
        if (plan.Target == "android-emulator" && plan.NeedsChanges && plan.Blockers.Count == 0 && plan.JavaPath is not null)
        {
            var java = await AndroidSetupProcess.RunAsync(plan.JavaPath, ["-version"], null, null, token, TimeSpan.FromSeconds(15));
            var version = Regex.Match(java.Output, @"version\s+""(?<major>\d+)");
            if (java.ExitCode != 0 || !version.Success || int.Parse(version.Groups["major"].Value) < 17)
                plan = plan with { Blockers = [.. plan.Blockers, "Android SDK command-line tools require a working JDK 17 or newer. Set JAVA_HOME and retry."] };
        }
        var emulatorPath = Path.Combine(plan.SdkRoot, "emulator", "emulator");
        if (plan.Target == "android-emulator" && plan.NeedsChanges && plan.Blockers.Count == 0 && File.Exists(emulatorPath))
        {
            var acceleration = await AndroidSetupProcess.RunAsync(emulatorPath, ["-accel-check"], null, null, token, TimeSpan.FromSeconds(15));
            var check = AndroidReadinessDoctor.CreateAccelerationCheck(acceleration.ExitCode == 0, acceleration.Output, emulatorPath);
            if (!check.IsSuccess) plan = plan with { Blockers = [.. plan.Blockers, check.Message] };
        }
        if (plan.Blockers.Count > 0)
        {
            WriteResult(output, plan, [], false, false, plan.Blockers, dryRun);
            return dryRun ? CliExitCodes.Success : CliExitCodes.CapabilityUnavailable;
        }
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new AndroidPackageCatalog(client);
        var packages = plan.MissingPackages.Count == 0 ? [] : await catalog.ResolveAsync(plan.MissingPackages, token);
        if (dryRun)
        {
            WriteResult(output, plan, packages, false, false, [], true);
            return CliExitCodes.Success;
        }
        if (plan.NeedsChanges)
        {
            output.WriteProgress(RenderPlan(plan, packages));
            if (!arguments.HasFlag("yes") && !Confirm("Install the missing Android components and create the listed AVD? [y/N] ", output, input, interactive && !arguments.IsJson))
                throw new CliUsageException("No Android changes were made. Run setup interactively or pass --yes; --dry-run previews the plan.");
            if (packages.Count > 0 && !arguments.HasFlag("accept-android-licenses"))
            {
                if (interactive && !arguments.IsJson)
                {
                    foreach (var license in packages.Select(package => package.License).Distinct()) output.WriteProgress(license);
                }
                if (!Confirm("Accept the Android SDK licenses for this setup? [y/N] ", output, input, interactive && !arguments.IsJson))
                    throw new CliUsageException("Android licenses were not accepted. Review licenses with --dry-run --json; pass --accept-android-licenses only after accepting them. --yes does not accept licenses.");
            }
            EnsureDiskSpace(plan.SdkRoot, packages.Sum(package => package.Bytes), plan.Target == "android-emulator");
            await ApplyAsync(plan, packages, catalog, output, token);
        }
        var checks = await AndroidReadinessDoctor.CheckAsync(plan.Target, plan.AdbPath, token);
        // A device need not be plugged in just to finish installing ADB.
        var toolsReady = checks.Where(check => check.Name != "android.connection").All(check => check.IsSuccess);
        if (toolsReady && File.Exists(Path.Combine(plan.SdkRoot, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb")))
            new LocalSettingsStore(options.DataDirectory).SetAndroidSdkRoot(plan.SdkRoot);
        var workflowReady = checks.All(check => check.IsSuccess);
        WriteResult(output, plan, packages, toolsReady, workflowReady,
            checks.Where(check => !check.IsSuccess).Select(check => check.Message).ToArray());
        return toolsReady ? CliExitCodes.Success : CliExitCodes.CapabilityUnavailable;
    }

    internal static async Task ApplyAsync(AndroidSetupPlan plan, IReadOnlyList<AndroidPackage> packages,
        AndroidPackageCatalog catalog, CliOutput output, CancellationToken token)
    {
        if (plan.Target == "android-device")
        {
            foreach (var package in packages)
            {
                output.WriteProgress($"Downloading and verifying {package.Name} ({package.Bytes / 1024 / 1024} MiB)...");
                await catalog.InstallArchiveAsync(package, plan.SdkRoot, token);
            }
            return;
        }
        var bootstrap = packages.FirstOrDefault(package => package.Name == AndroidSetupPlan.CommandToolsPackage);
        if (bootstrap is not null)
        {
            output.WriteProgress("Installing Android SDK command-line tools 19.0...");
            await catalog.InstallArchiveAsync(bootstrap, plan.SdkRoot, token);
        }
        var sdkManager = AndroidSetupPlan.FindManager(plan.SdkRoot, "sdkmanager")
            ?? throw new InvalidOperationException("Android SDK manager is still unavailable.");
        var avdManager = AndroidSetupPlan.FindManager(plan.SdkRoot, "avdmanager")
            ?? throw new InvalidOperationException("Android virtual-device manager is still unavailable.");
        var install = packages.Where(package => package.Name != AndroidSetupPlan.CommandToolsPackage).Select(package => package.Name).ToArray();
        if (install.Length > 0)
        {
            output.WriteProgress("Installing missing Android packages. This may take several minutes...");
            // Acceptance is explicitly authorized above; it is never implied by --yes.
            var tools = install.Where(package => !package.StartsWith("system-images;", StringComparison.Ordinal)).ToArray();
            if (tools.Length > 0)
                await RequireSuccessAsync(sdkManager, [$"--sdk_root={plan.SdkRoot}", "--install", .. tools], plan.JavaPath, string.Concat(Enumerable.Repeat("y\n", 100)), plan.SdkRoot, token);
            var images = install.Where(package => package.StartsWith("system-images;", StringComparison.Ordinal)).ToArray();
            if (images.Length > 0)
            {
                var executable = Path.Combine(plan.SdkRoot, "emulator", "emulator");
                var acceleration = await AndroidSetupProcess.RunAsync(executable, ["-accel-check"], null, null, token, TimeSpan.FromSeconds(15));
                var check = AndroidReadinessDoctor.CreateAccelerationCheck(acceleration.ExitCode == 0, acceleration.Output, executable);
                if (!check.IsSuccess) throw new InvalidOperationException(check.Message);
                await RequireSuccessAsync(sdkManager, [$"--sdk_root={plan.SdkRoot}", "--install", .. images], plan.JavaPath, string.Concat(Enumerable.Repeat("y\n", 100)), plan.SdkRoot, token);
            }
        }
        if (plan.CreateAvd)
        {
            // No --force: an AVD created concurrently must be preserved too.
            output.WriteProgress($"Creating baseline virtual device {plan.AvdName}...");
            await RequireSuccessAsync(avdManager, ["create", "avd", "--name", plan.AvdName, "--package", AndroidSetupPlan.BaselineImage],
                plan.JavaPath, "no\n", plan.SdkRoot, token);
        }
    }

    private static async Task RequireSuccessAsync(string executable, string[] arguments, string? java, string? stdin, string sdkRoot, CancellationToken token)
    {
        var result = await AndroidSetupProcess.RunAsync(executable, arguments, java, stdin, token, sdkRoot: sdkRoot);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Android setup failed running {Path.GetFileName(executable)} (exit {result.ExitCode}). {result.Output}");
    }

    internal static void EnsureDiskSpace(string sdkRoot, long downloadBytes, bool emulator = false)
    {
        var existing = Path.GetFullPath(sdkRoot);
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing)
            ?? throw new IOException("Cannot find a parent directory for the Android SDK.");
        var required = Math.Max(checked(downloadBytes * 3 + 1024L * 1024 * 1024), emulator ? 8L * 1024 * 1024 * 1024 : 0);
        if (new DriveInfo(existing).AvailableFreeSpace < required)
            throw new IOException($"Android setup needs at least {required / 1024 / 1024} MiB free for downloads and extraction.");
    }

    private static bool Confirm(string prompt, CliOutput output, TextReader? input, bool interactive)
    {
        if (!interactive) return false;
        output.WritePrompt(prompt);
        return input?.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static void WriteResult(CliOutput output, AndroidSetupPlan plan, IReadOnlyList<AndroidPackage> packages,
        bool toolsReady, bool workflowReady, IReadOnlyList<string> messages, bool preview = false)
        => output.Write(new { Schema = "ansight.android-setup/v1", Plan = plan, Downloads = packages, IsDryRun = preview, ToolsReady = toolsReady, WorkflowReady = workflowReady, Messages = messages },
            () => RenderPlan(plan, packages) + (preview ? "\nPreview only; no changes made and readiness not verified.\n" : $"\nAndroid tools ready: {toolsReady}\nTarget prerequisites ready: {workflowReady}\n")
                + string.Join('\n', messages) + "\nApp boot, screenshot capture, and input have not been tested. Run ansight doctor --target " + plan.Target + ".");

    private static string RenderPlan(AndroidSetupPlan plan, IReadOnlyList<AndroidPackage> packages)
        => $"Android setup: {plan.Target}\nSDK: {plan.SdkRoot}\n"
            + (plan.NeedsChanges ? "Missing packages: " + string.Join(", ", plan.MissingPackages) : "Existing Android tools will be reused.")
            + (plan.MissingPackages.Count > 0 && packages.Count == 0 ? "\nDownload size: not resolved (prerequisites incomplete)"
                : $"\nDownload size: {packages.Sum(package => package.Bytes) / 1024 / 1024} MiB")
            + (plan.CreateAvd ? $"\nCreate AVD: {plan.AvdName} (Android 35, Google APIs, x86_64)" : "\nExisting virtual devices will be preserved.");

    private const string Help = """
        Optional machine setup; available before account sign-in

        Usage:
          ansight setup android [--target android-device|android-emulator] [options]
          ansight setup credentials [--key-file <path>] [--non-interactive]

        Android defaults to external-device tools (ADB). Existing SDKs and AVDs are reused.
        Automatic downloads support Linux x64; local emulator setup also requires JDK 17+
        and usable KVM. Setup never changes virtualization, group membership, or system packages.

        Options:
          --sdk-root <path>          Select an existing SDK or destination for missing tools
          --avd-name <name>          Baseline AVD name (default: ansight-api35); never overwritten
          --dry-run                 Show discovery, blockers, package sizes, and license text in JSON
          --yes                     Apply the displayed plan without a confirmation prompt
          --accept-android-licenses Explicitly accept Android SDK licenses; separate from --yes
          --json                    Machine-readable plan or result; prompts are disabled

        Examples:
          ansight setup android --dry-run --json
          ansight setup android --target android-emulator
          ansight setup android --target android-device --yes --accept-android-licenses

        Uses existing Google SDK command-line tools when available, or a pinned 19.0 bootstrap.
        Packages are downloaded from Google's HTTPS repository and verified against its checksums.
        SDK selection is saved for CLI and host use after validation. --sdk-root does not alter
        your shell or Android Studio settings. Local app execution needs no Ansight account.
        """;
}
