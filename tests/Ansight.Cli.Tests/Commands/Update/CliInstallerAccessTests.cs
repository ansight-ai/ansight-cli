using System.Diagnostics;

namespace Ansight.Cli.Tests.Commands.Update;

public sealed class CliInstallerAccessTests
{
    [Fact]
    public async Task LinuxUpgradeAddsAccessExitCodeWithoutEnablingOrDuplicatingTheService()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var unit = Path.Combine(directory.Path, ".config", "systemd", "user", "ansight-host.service");
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        var executable = Path.Combine(directory.Path, "path with spaces", "current");
        await File.WriteAllTextAsync(unit, $"[Service]\nExecStart=\"{executable}/ansight\" host run\nRestart=on-failure\nRestartSec=5\n");
        await RunMigrationAsync(directory.Path, executable);
        var migrated = await File.ReadAllTextAsync(unit);
        Assert.Contains("RestartPreventExitStatus=6", migrated);
        await RunMigrationAsync(directory.Path, executable);
        Assert.Equal(migrated, await File.ReadAllTextAsync(unit));
    }

    [Fact]
    public async Task LinuxUpgradeDoesNotRewriteCustomServicesOrCreateMissingOnes()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var executable = Path.Combine(directory.Path, "current");
        await RunMigrationAsync(directory.Path, executable);
        var unit = Path.Combine(directory.Path, ".config", "systemd", "user", "ansight-host.service");
        Assert.False(File.Exists(unit));
        Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
        const string custom = "[Service]\nExecStart=/custom/ansight host run\nRestart=on-failure\n";
        await File.WriteAllTextAsync(unit, custom);
        await RunMigrationAsync(directory.Path, executable);
        Assert.Equal(custom, await File.ReadAllTextAsync(unit));
    }

    private static async Task RunMigrationAsync(string testDirectory, string executable)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "install", "install.sh")))
            root = root.Parent;
        Assert.NotNull(root);
        var installer = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "install", "install.sh"));
        var start = installer.IndexOf("# Upgrade our existing service", StringComparison.Ordinal);
        var end = installer.IndexOf("if [[ \"${SKIP_SETUP}\" -eq 0 && \"${CREDENTIALS_READY}\" -eq 1 ]]; then", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        // Exercise the real migration, isolated from the workstation's home and service manager.
        var script = "set -eu\nsystemctl() { test \"$*\" = '--user daemon-reload'; }\n"
                     + installer[start..end].Replace("${HOME}", "${TEST_INSTALL_HOME}", StringComparison.Ordinal);
        var info = new ProcessStartInfo("bash")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        info.Environment["TEST_INSTALL_HOME"] = testDirectory;
        info.Environment["CURRENT_LINK"] = executable;
        info.Environment["PLATFORM"] = "linux";
        using var process = Process.Start(info)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await standardOutput + await standardError);
    }
}
