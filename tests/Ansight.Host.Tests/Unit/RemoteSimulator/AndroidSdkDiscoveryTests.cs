using Ansight.Adb;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AndroidSdkDiscoveryTests
{
    [Fact]
    public void DiscoversSdkFromManagerOnPathAndDeduplicatesIt()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "existing SDK");
        var bin = Path.Combine(sdk, "cmdline-tools", "19.0", "bin");
        Directory.CreateDirectory(bin);
        var suffix = OperatingSystem.IsWindows() ? ".bat" : string.Empty;
        File.WriteAllText(Path.Combine(bin, "sdkmanager" + suffix), "fixture");
        File.WriteAllText(Path.Combine(bin, "avdmanager" + suffix), "fixture");
        Assert.Equal([sdk], AndroidSdkDiscovery.FromPath(bin + Path.PathSeparator + bin).ToArray());
    }

    [Fact]
    public void FindsEmulatorBesideSymlinkedAdb()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var platformTools = Path.Combine(sdk, "platform-tools");
        var emulatorDirectory = Path.Combine(sdk, "emulator");
        Directory.CreateDirectory(platformTools);
        Directory.CreateDirectory(emulatorDirectory);
        var adb = Path.Combine(platformTools, "adb");
        var emulator = Path.Combine(emulatorDirectory, "emulator");
        File.WriteAllText(adb, "fixture");
        File.WriteAllText(emulator, "fixture");
        var link = Path.Combine(directory.Path, "adb-link");
        File.CreateSymbolicLink(link, adb);
        Assert.Equal(sdk, AndroidSdkDiscovery.FromTool(link));
        var resolution = AndroidEmulatorToolLocator.Resolve(link);
        Assert.True(resolution.IsFound);
        Assert.Equal(emulator, resolution.EmulatorPath);
        Assert.Equal("ADB SDK", resolution.Source);
    }

    [Fact]
    public void MissingToolsAndUnrelatedDirectoriesDoNotBecomeSdkRoots()
    {
        using var directory = TestDirectory.Create();
        var bin = Path.Combine(directory.Path, "bin");
        Directory.CreateDirectory(bin);
        var name = OperatingSystem.IsWindows() ? "sdkmanager.bat" : "sdkmanager";
        File.WriteAllText(Path.Combine(bin, name), "fixture");
        Assert.Empty(AndroidSdkDiscovery.FromPath(bin));
        Assert.Null(AndroidSdkDiscovery.FromTool(Path.Combine(bin, "missing")));
    }
}
