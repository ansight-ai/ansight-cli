using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Ansight.Cli.Commands.Setup;

namespace Ansight.Cli.Tests.Commands.Setup;

public sealed class AndroidSetupTests
{
    [Fact]
    public async Task FalseLicenseFlagCannotBeTreatedAsAcceptance()
    {
        await Assert.ThrowsAsync<CliUsageException>(() => SetupCommands.RunAsync(
            CliArguments.Parse(["setup", "android", "--accept-android-licenses=false"]),
            new CliOutput(false, new StringWriter(), new StringWriter()), null, false, CancellationToken.None));
    }

    [Fact]
    public void ExistingSdkAndAvdNeedNoDownloadsOrReplacement()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var avds = Path.Combine(directory.Path, "avd");
        Touch(Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb"));
        Touch(Path.Combine(sdk, "emulator", OperatingSystem.IsWindows() ? "emulator.exe" : "emulator"));
        Touch(Path.Combine(sdk, "system-images", "existing", "system.img"));
        Touch(Path.Combine(avds, "Custom.ini"));
        Touch(Path.Combine(avds, "Custom.avd", "config.ini"), "image.sysdir.1=system-images/existing\n");
        var plan = AndroidSetupPlan.Discover(CliArguments.Parse(["setup", "android", "--target", "android-emulator", "--sdk-root", sdk]), null, avds);
        Assert.False(plan.NeedsChanges);
        Assert.Empty(plan.MissingPackages);
        Assert.False(plan.CreateAvd);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public void ExternalDevicePlanOnlyNeedsPlatformToolsAndDiscoveryDoesNotCreateDirectories()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "new-sdk");
        var plan = AndroidSetupPlan.Discover(CliArguments.Parse(["setup", "android", "--sdk-root", sdk]), null);
        Assert.Equal(["platform-tools"], plan.MissingPackages);
        Assert.False(plan.CreateAvd);
        Assert.False(Directory.Exists(sdk));
    }

    [Fact]
    public void MissingImageCannotCauseAnExistingAvdToBeOverwritten()
    {
        using var directory = TestDirectory.Create();
        var avds = Path.Combine(directory.Path, "avd");
        Touch(Path.Combine(avds, "ansight-api35.ini"));
        var plan = AndroidSetupPlan.Discover(CliArguments.Parse(["setup", "android", "--target", "android-emulator", "--sdk-root", Path.Combine(directory.Path, "sdk")]), null, avds);
        Assert.Contains(plan.Blockers, message => message.Contains("will not overwrite"));
    }

    [Fact]
    public async Task SetupHelpIsAvailableWithoutAccountOrResidentHost()
    {
        using var directory = TestDirectory.Create();
        var data = Path.Combine(directory.Path, "state");
        using var stdout = new StringWriter();
        var arguments = CliArguments.Parse(["setup", "android", "--help", "--data-dir", data]);
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(arguments));
        var result = await CliApplication.RunParsedAsync(arguments, new CliOutput(false, stdout, new StringWriter()),
            CancellationToken.None, true, accessAuthorizer: TestAccessAuthorizer.Deny,
            residentHostForwarder: (_, _, _) => throw new Exception("Setup must stay local"));
        Assert.Equal(0, result);
        Assert.Contains("--accept-android-licenses", stdout.ToString());
        Assert.False(Directory.Exists(data));
        Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(CliArguments.Parse(["setup", "android"])));
    }

    [Fact]
    public async Task DryRunStaysLocalAndDoesNotSaveSettings()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var data = Path.Combine(directory.Path, "state");
        Touch(Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb"));
        using var stdout = new StringWriter();
        var result = await CliApplication.RunParsedAsync(
            CliArguments.Parse(["setup", "android", "--sdk-root", sdk, "--data-dir", data, "--dry-run", "--json"]),
            new CliOutput(true, stdout, new StringWriter()), CancellationToken.None, true,
            accessAuthorizer: TestAccessAuthorizer.Deny,
            residentHostForwarder: (_, _, _) => throw new Exception("Setup must stay local"));
        Assert.Equal(0, result);
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("isDryRun").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("downloads").EnumerateArray());
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public async Task EmulatorSetupUsesSelectedSdkAndDoesNotForceOverwriteAvds()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk with spaces");
        var sdkManager = Path.Combine(sdk, "cmdline-tools", "19.0", "bin", "sdkmanager");
        var avdManager = Path.Combine(sdk, "cmdline-tools", "19.0", "bin", "avdmanager");
        Touch(sdkManager, "#!/bin/sh\ncat >/dev/null\nprintf '%s\\n' \"$*\" >> \"$ANDROID_SDK_ROOT/calls\"\n");
        Touch(avdManager, "#!/bin/sh\ncat >/dev/null\n[ \"$ANDROID_HOME\" = \"$ANDROID_SDK_ROOT\" ] || exit 8\nprintf '%s\\n' \"$*\" >> \"$ANDROID_SDK_ROOT/calls\"\n");
        var emulator = Path.Combine(sdk, "emulator", "emulator");
        Touch(emulator, "#!/bin/sh\nprintf 'accel:\\n0\\nKVM usable\\n'\n");
        foreach (var executable in new[] { sdkManager, avdManager, emulator })
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plan = new AndroidSetupPlan("android-emulator", sdk, Path.Combine(sdk, "platform-tools", "adb"),
            sdkManager, avdManager, null, "ansight-api35", [AndroidSetupPlan.BaselineImage], true, []);
        using var client = new HttpClient(new BytesHandler([]));
        var package = new AndroidPackage(AndroidSetupPlan.BaselineImage, new("https://dl.google.com/android/repository/fixture.zip"), 1, new string('a', 40), "test");
        await SetupCommands.ApplyAsync(plan, [package], new AndroidPackageCatalog(client), new CliOutput(false, new StringWriter(), new StringWriter()), CancellationToken.None);
        var calls = File.ReadAllText(Path.Combine(sdk, "calls"));
        Assert.Contains("--sdk_root=" + sdk, calls);
        Assert.Contains("--install " + AndroidSetupPlan.BaselineImage, calls);
        Assert.Contains("create avd --name ansight-api35 --package " + AndroidSetupPlan.BaselineImage, calls);
        Assert.DoesNotContain("--force", calls);
    }

    [Theory]
    [InlineData("https://attacker.example/package.zip")]
    [InlineData("http://dl.google.com/android/repository/package.zip")]
    public void PackageCatalogRejectsUntrustedDownloadLocations(string url)
    {
        var xml = Manifest(url);
        Assert.Throws<InvalidDataException>(() => AndroidPackageCatalog.ParsePackage(xml, "platform-tools", new("https://dl.google.com/android/repository/repository2-3.xml")));
    }

    [Fact]
    public void PackageCatalogRequiresSizeChecksumAndLicense()
    {
        var package = AndroidPackageCatalog.ParsePackage(Manifest("package.zip"), "platform-tools", new("https://dl.google.com/android/repository/repository2-3.xml"));
        Assert.Equal("https://dl.google.com/android/repository/package.zip", package.Download.AbsoluteUri);
        Assert.Equal("License for test", package.License);
        var bad = Manifest("package.zip");
        bad.Descendants("checksum").Single().Value = "bad";
        Assert.Throws<InvalidDataException>(() => AndroidPackageCatalog.ParsePackage(bad, "platform-tools", package.Download));
    }

    [Fact]
    public async Task VerifiedArchiveInstallsAtomicallyAndPreservesExistingTools()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var bytes = Archive("platform-tools/adb", "fixture");
        using var client = new HttpClient(new BytesHandler(bytes));
        var catalog = new AndroidPackageCatalog(client);
        var package = Package(bytes);
        await catalog.InstallArchiveAsync(package, sdk, CancellationToken.None);
        var adb = Path.Combine(sdk, "platform-tools", "adb");
        Assert.Equal("fixture", File.ReadAllText(adb));
        Assert.Empty(Directory.EnumerateDirectories(sdk, ".ansight-download-*"));
        await Assert.ThrowsAsync<IOException>(() => catalog.InstallArchiveAsync(package, sdk, CancellationToken.None));
        Assert.Equal("fixture", File.ReadAllText(adb));
    }

    [Fact]
    public async Task BadChecksumLeavesNoInstalledPackage()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var bytes = Archive("platform-tools/adb", "fixture");
        using var client = new HttpClient(new BytesHandler(bytes));
        var catalog = new AndroidPackageCatalog(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.InstallArchiveAsync(Package(bytes) with { Sha1 = new string('0', 40) }, sdk, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(sdk, "platform-tools")));
        Assert.Empty(Directory.EnumerateDirectories(sdk));
    }

    [Fact]
    public async Task ArchiveCannotEscapeExtractionDirectory()
    {
        using var directory = TestDirectory.Create();
        var sdk = Path.Combine(directory.Path, "sdk");
        var bytes = Archive("../../../escaped", "fixture");
        using var client = new HttpClient(new BytesHandler(bytes));
        var catalog = new AndroidPackageCatalog(client);
        await Assert.ThrowsAsync<IOException>(() => catalog.InstallArchiveAsync(Package(bytes), sdk, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory.Path, "escaped")));
        Assert.Empty(Directory.EnumerateDirectories(sdk));
    }

    [Fact]
    public void SavedSdkIsUsedByAFreshCliRuntime()
    {
        using var directory = TestDirectory.Create();
        var data = Path.Combine(directory.Path, "state");
        var sdk = Path.Combine(directory.Path, "sdk");
        new LocalSettingsStore(data).SetAndroidSdkRoot(sdk);
        Assert.Equal(sdk, LocalSettingsStore.ReadAndroidSdkRoot(data));
        // Explicit command options must remain authoritative over saved defaults.
        var options = CliRuntime.ResolveOptions(CliArguments.Parse(["doctor", "--data-dir", data, "--adb-path", "/explicit/adb"]));
        Assert.Equal("/explicit/adb", options.AdbPath);
    }

    private static AndroidPackage Package(byte[] bytes)
        => new("platform-tools", new("https://dl.google.com/android/repository/fixture.zip"), bytes.Length,
            Convert.ToHexString(SHA1.HashData(bytes)), "License for test");

    private static byte[] Archive(string name, string value)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(value);
        return stream.ToArray();
    }

    private static XDocument Manifest(string url)
        => new(new XElement("repository",
            new XElement("license", new XAttribute("id", "test"), "License for test"),
            new XElement("remotePackage", new XAttribute("path", "platform-tools"),
                new XElement("uses-license", new XAttribute("ref", "test")),
                new XElement("archives", new XElement("archive", new XElement("host-os", "linux"),
                    new XElement("complete", new XElement("url", url), new XElement("size", 100), new XElement("checksum", new string('a', 40))))))));

    private static void Touch(string path, string value = "fixture")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private sealed class BytesHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }
}
