using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using System.IO.Compression;
using System.Text.Json;
using Ansight.Infrastructure.Security;
using Ansight.Cli.Commands.ArtifactComparison;
using Ansight.Host.Files;

namespace Ansight.Cli.Tests.Commands.ArtifactComparison;

public sealed class ArtifactCommandsTests
{
    [Fact]
    public void OrderedQualifiedReferencesPreserveRepeatedCaptures()
    {
        var arguments = CliArguments.Parse(["artifact", "diff", "--artifact", "baseline/A", "--artifact", "rerun/B", "--artifact", "baseline/A"]);
        Assert.Equal(new[] { "baseline/A", "rerun/B", "baseline/A" }, ArtifactCommands.ReadReferences(arguments).Select(item => item.Reference));
        Assert.True(ControlClient.ShouldForward(arguments));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("/A")]
    [InlineData("s/")]
    [InlineData("s/A/extra")]
    public void RejectsUnqualifiedOrMalformedReferences(string reference)
        => Assert.Throws<CliUsageException>(() => ArtifactCommands.ReadReferences(CliArguments.Parse(["artifact", "diff", "--artifact", "s/A", "--artifact", reference])));

    [Fact]
    public void RejectsMissingReferenceRatherThanDroppingHop()
        => Assert.Throws<CliUsageException>(() => ArtifactCommands.ReadReferences(CliArguments.Parse(["artifact", "diff", "--artifact", "s/A", "--artifact", "s/B", "--artifact", "--json"])));

    [Fact]
    public void ListingShowsCopyableReferenceWithoutJson()
    {
        var item = new ArtifactListItem("baseline", "Baseline", "A", "baseline/A", "State", "provider", "state", DateTimeOffset.UtcNow, 1, 42, false, ["json"], ["state.json"]);
        var text = ArtifactCommands.FormatList(new("ansight.artifact-list/v1", [item], 2, 1));
        Assert.Contains("baseline/A", text); Assert.Contains("--offset 1", text);
    }
    [Fact]
    public async Task DiscoveryOutputFeedsDiffCommandThroughResidentRuntime()
    {
        using var directory = TestDirectory.Create();
        var store = Path.Combine(directory.Path, "secrets.json");
        var key = Path.Combine(directory.Path, "secrets.key");
        FileEncryptionKeyProvider.CreateKeyFile(key);
        string[] runtimeArgs = ["--data-dir", directory.Path, "--secret-store-file", store, "--secret-key-file", key, "--json"];
        var options = CliRuntime.ResolveOptions(CliArguments.Parse(runtimeArgs));
        await using var lease = await CliRuntimeLease.CreateAsync(options, false, default);
        var now = DateTimeOffset.UtcNow;
        var captures = new[] { "before", "after" }.Select(id => new SessionArtifactSnapshot
        {
            SnapshotId = id, CapturedAtUtc = id == "before" ? now : now.AddSeconds(1), Source = "test", RootAlias = "state", RelativePath = "scene", Name = "Scene",
            Kind = "file", ArtifactDirectoryName = id, FileCount = 1, ByteCount = 7,
            Entries = [new SessionArtifactEntry { Name = "state.json", RootAlias = "state", RelativePath = "scene", SnapshotRelativePath = "state.json", ArchiveRelativePath = "state.json", Kind = "file", MimeType = "application/json", FileExtension = ".json", SizeBytes = 7 }]
        }).ToArray();
        var session = new AppSessionSnapshot
        {
            SessionId = "cli-artifacts", AppId = "test", ClientName = "test", RemoteAddress = "127.0.0.1", CreatedUtc = now, LastUpdatedUtc = now,
            ConfigId = null, Status = "Complete", IsHistorical = true, MetricChannels = [], Metrics = [], ArtifactSnapshots = captures
        };
        var archivePath = Path.Combine(directory.Path, "capture.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using var content = archive.CreateEntry("session.json").Open();
            await JsonSerializer.SerializeAsync(content, new SessionCaptureDocument { SavedAtUtc = now, Session = session }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        var imported = Assert.IsType<AppSessionSnapshot>((await lease.Runtime.SessionArchives.ImportSessionArchiveAsync(archivePath)).ImportedSession);
        foreach (var capture in imported.ArtifactSnapshots)
        {
            var path = SessionFileLocator.ResolveArtifactSnapshotDirectoryPath(lease.Runtime.ApplicationPaths, imported, capture);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "state.json"), capture.SnapshotId == "before" ? "{\"x\":0}" : "{\"x\":1}");
        }
        using var context = CliCommandContext.Push(lease.Runtime, options.DataDirectory, null);
        using var listOutput = new StringWriter(); using var errors = new StringWriter();
        var listExit = await CliApplication.RunParsedAsync(CliArguments.Parse(["artifact", "list", "--session-id", imported.SessionId, .. runtimeArgs]), new CliOutput(true, listOutput, errors), default, false, accessAuthorizer: TestAccessAuthorizer.Allow);
        Assert.Equal(0, listExit);
        var listed = JsonSerializer.Deserialize<ArtifactListResult>(listOutput.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, listed.Items.Count);
        using var diffOutput = new StringWriter();
        var diffExit = await CliApplication.RunParsedAsync(CliArguments.Parse(["artifact", "diff", "--artifact", listed.Items[0].Reference, "--artifact", listed.Items[1].Reference, "--fail-on-change", .. runtimeArgs]), new CliOutput(true, diffOutput, errors), default, false, accessAuthorizer: TestAccessAuthorizer.Allow);
        Assert.Equal(12, diffExit);
        var result = JsonSerializer.Deserialize<ArtifactDiffResult>(diffOutput.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("changed", Assert.Single(result.Hops).Status);
        Assert.Equal("1", Assert.Single(Assert.Single(result.Hops[0].Files).Changes).After);
        Assert.Equal("", errors.ToString());
    }
}
