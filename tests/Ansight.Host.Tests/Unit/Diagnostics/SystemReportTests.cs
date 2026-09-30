using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Diagnostics;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.Diagnostics;

public sealed class SystemReportTests
{
    [Fact]
    public void OptedInSecretInventoryIncludesRecordedRevisionWithoutValues()
    {
        var store = new SecretStore(new InMemoryEncryptedStorage());
        var metadata = store.Set("example.app", "TEST_PASSWORD", "private-value");
        var section = SystemReportService.CollectSecretMetadata(true,
            () => SystemReportService.Node(store.ListForDiagnostics("example.app")));
        var result = Assert.Single(section.Data!.AsArray());
        Assert.Equal("complete", section.Status);
        Assert.Equal("TEST_PASSWORD", result!["alias"]!.ToString());
        Assert.Equal(metadata.VersionId, result["versionId"]!.ToString());
        Assert.DoesNotContain("private-value", section.Data.ToJsonString());
    }

    [Theory]
    [InlineData("dotnet-dsrouter", "WARNING: dotnet-dsrouter is a development tool.\n9.0.661903+abc", "9.0.661903+abc")]
    [InlineData("adb", "Android Debug Bridge version 1.0.41\nVersion 37.0.0-123", "37.0.0-123")]
    [InlineData("node", "v24.3.0", "24.3.0")]
    public void VersionsExcludeBanners(string name, string output, string expected)
        => Assert.Equal(expected, DiagnosticProcess.ParseVersion(name, output));

    [Fact]
    public void ReadOnlySecretSnapshotDoesNotMigrateOrCreateLockFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-secret-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "legacy.json");
        const string original = "{\"example\":\"private-value\"}";
        File.WriteAllText(file, original);
        try
        {
            var storage = FileBackedEncryptedStorage.OpenReadOnly(file, new byte[32]);
            Assert.Equal("private-value", storage.Get("example"));
            Assert.Equal(original, File.ReadAllText(file));
            Assert.Single(Directory.GetFiles(root));
            Assert.Throws<InvalidOperationException>(() => storage.Set("example", "changed"));
            Assert.Throws<InvalidOperationException>(() => storage.Remove("example"));
            Assert.Throws<InvalidOperationException>(() => storage.Clear());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OptedOutSecretInventoryDoesNotCallCollectorOrDiscloseItsFailure()
    {
        var calls = 0;
        var section = SystemReportService.CollectSecretMetadata(false, () =>
        {
            calls++;
            throw new InvalidOperationException("customer-password");
        });
        Assert.Equal(0, calls);
        Assert.Equal("not-requested", section.Status);
        Assert.Null(section.Data);
        Assert.DoesNotContain("customer-password", JsonSerializer.Serialize(section));
    }

    [Fact]
    public void UnavailableSecretMetadataIsNotReportedAsAnEmptyStore()
    {
        var section = SystemReportService.CollectSecretMetadata(true, () => throw new IOException("private-alias"));
        Assert.Equal("not-checked", section.Status);
        Assert.Null(section.Data);
        Assert.DoesNotContain("private-alias", section.Message);
    }

    [Fact]
    public void SkillVerificationFindsChangedMissingAndExtraSupportingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-skills-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", "ansight-test");
        var installed = Path.Combine(root, "installed", "ansight-test");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        Directory.CreateDirectory(Path.Combine(installed, "references"));
        try
        {
            File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: ansight-test\nmetadata:\n  version: 1.0.0\n---\ninstructions");
            File.WriteAllText(Path.Combine(source, "references", "usage.md"), "reference");
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(installed, Path.GetRelativePath(source, file)));
            var initial = SkillInventory.Inspect(installed, "test", source);
            Assert.Equal("matches-bundled", initial["comparison"]!.ToString());
            Assert.Equal("1.0.0", initial["version"]!.ToString());
            File.WriteAllText(Path.Combine(installed, "SKILL.md"), "changed");
            File.Delete(Path.Combine(installed, "references", "usage.md"));
            File.WriteAllText(Path.Combine(installed, "extra.txt"), "unexpected");
            var report = SkillInventory.Inspect(installed, "test", source);
            Assert.Equal("differs-from-bundled", report["comparison"]!.ToString());
            Assert.Equal(3, report["differences"]!.AsArray().Count);
            Assert.NotEqual(report["bundledSha256"]!.ToString(), report["sha256"]!.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ObservedProfileExtractsVersionsWithoutCopyingArbitraryProfileData()
    {
        var profile = JsonNode.Parse("""
            {"app":{"appId":"example","versionName":"1.2","buildNumber":"34","custom":"private"},
             "sdk":{"version":"2.0"},"device":{"osName":"iOS","osVersion":"26.0","network":{"token":"private"}},
             "runtime":{"primaryVersion":"10.0"},"tags":{"secret":"private"}}
            """)!.AsObject();
        var wrapped = new JsonObject { ["ProfileJson"] = profile.ToJsonString() };
        var result = SystemReportService.SelectObservedProfile(wrapped);
        Assert.Equal("1.2", result["app"]!["versionName"]!.ToString());
        Assert.Equal("34", result["app"]!["buildNumber"]!.ToString());
        Assert.Equal("2.0", result["sdk"]!["version"]!.ToString());
        Assert.Equal("26.0", result["device"]!["osVersion"]!.ToString());
        Assert.Equal("10.0", result["runtime"]!["primaryVersion"]!.ToString());
        Assert.DoesNotContain("private", result.ToJsonString());
        Assert.Equal(result.ToJsonString(), SystemReportService.SelectObservedProfile(profile).ToJsonString());
    }

    [Fact]
    public void PackageDigestIncludesFileNamesAndIgnoresEnumerationOrder()
    {
        SkillFileDigest[] files = [new("a.md", 1, "aaa"), new("b.md", 1, "bbb")];
        Assert.Equal(SkillInventory.PackageHash(files), SkillInventory.PackageHash(files.Reverse()));
        Assert.NotEqual(SkillInventory.PackageHash(files), SkillInventory.PackageHash([files[0] with { Path = "renamed.md" }, files[1]]));
    }
}
