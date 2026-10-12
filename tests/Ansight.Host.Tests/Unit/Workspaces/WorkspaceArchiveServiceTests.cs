using System.IO.Compression;
using System.Text;
using Ansight.Host.Tests.Unit.Runtime;
using Ansight.Host.Workspaces.Archives;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceArchiveServiceTests
{
    [Fact]
    public void ExportImport_RoundTripsDefinitionsSupportFilesFixturesAndEmptyDirectories()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.RootPath, "source");
        var destination = Path.Combine(directory.RootPath, "destination");
        var zip = Path.Combine(directory.RootPath, "workspace.zip");
        var authoring = new WorkspaceAuthoringService();
        Assert.True(authoring.Initialize(new(source)).IsSuccess);
        Assert.True(authoring.AddTest(new(source, "smoke", "com.example.app")).IsSuccess);
        Assert.True(authoring.AddTask(new(source, "open-app")).IsSuccess);
        Write(source, "ansight/tasks/helpers/data.bin", [0, 1, 255, 27]);
        Write(source, "ansight/trends/startup.json", Encoding.UTF8.GetBytes("{}"));
        Directory.CreateDirectory(Path.Combine(source, "ansight", "fixtures", "empty"));
        var expected = Directory.GetFiles(Path.Combine(source, "ansight"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), File.ReadAllBytes);
        Write(source, "app.cs", [1]);
        Write(source, "ansight/node_modules/ignored.js", [1]);
        Write(source, "ansight/.git/config", [1]);
        Write(source, "ansight/tasks/.DS_Store", [1]);

        var service = new WorkspaceArchiveService();
        var exported = service.Export(source, zip);
        var imported = service.Import(zip, destination);

        Assert.True(exported.IsSuccess, exported.Message);
        Assert.True(imported.IsSuccess, imported.Message);
        Assert.Equal(expected.Count, exported.FileCount);
        Assert.Equal(exported.FileCount, imported.FileCount);
        Assert.Equal(expected.Values.Sum(bytes => (long)bytes.Length), imported.ExpandedBytes);
        Assert.Equal(exported.ExpandedBytes, imported.ExpandedBytes);
        Assert.Equal(expected.Count, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        foreach (var item in expected) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(destination, item.Key)));
        Assert.True(Directory.Exists(Path.Combine(destination, "ansight", "fixtures", "empty")));
        using var archive = ZipFile.OpenRead(zip);
        Assert.All(archive.Entries, entry => Assert.StartsWith("ansight/", entry.FullName));
    }

    [Fact]
    public void Import_PreflightsAllConflicts_AndForcePreservesUnrelatedFiles()
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/new.txt", "ansight/existing.txt");
        var destination = Path.Combine(directory.RootPath, "destination");
        Write(destination, "ansight/existing.txt", Encoding.UTF8.GetBytes("original"));
        Write(destination, "ansight/keep.txt", [42]);
        var service = new WorkspaceArchiveService();

        var refused = service.Import(zip, destination);

        Assert.False(refused.IsSuccess);
        Assert.Contains("--force", refused.Message);
        Assert.False(File.Exists(Path.Combine(destination, "ansight", "new.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "ansight", "existing.txt")));
        var imported = service.Import(zip, destination, overwrite: true);
        Assert.True(imported.IsSuccess, imported.Message);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(destination, "ansight", "existing.txt")));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(destination, "ansight", "keep.txt")));
    }

    [Fact]
    public void Export_ProtectsExistingArchiveEvenWhenPackagingFails()
    {
        using var directory = new TemporaryDirectory();
        Write(directory.RootPath, "ansight/file.txt", Encoding.UTF8.GetBytes("contents"));
        var zip = Path.Combine(directory.RootPath, "workspace.zip");
        File.WriteAllText(zip, "original");
        Assert.False(new WorkspaceArchiveService().Export(directory.RootPath, zip).IsSuccess);
        Assert.Equal("original", File.ReadAllText(zip));
        var failed = new WorkspaceArchiveService(100, 2).Export(directory.RootPath, zip, overwrite: true);
        Assert.False(failed.IsSuccess);
        Assert.Equal("original", File.ReadAllText(zip));
        Assert.True(new WorkspaceArchiveService().Export(directory.RootPath, zip, overwrite: true).IsSuccess);
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("ansight/file.txt"));
        Assert.Empty(Directory.GetFiles(directory.RootPath, ".ansight-export-*"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("ansight/../../outside.txt")]
    [InlineData("/ansight/file.txt")]
    [InlineData("C:/ansight/file.txt")]
    [InlineData("ansight\\file.txt")]
    [InlineData("ansight/file:stream")]
    [InlineData("ansight/./file.txt")]
    [InlineData("ansight//file.txt")]
    [InlineData("ansight/CON.txt")]
    [InlineData("ansight/file. ")]
    [InlineData("ansight/node_modules/file.js")]
    [InlineData("ansight/.git/config")]
    [InlineData("ansight")]
    [InlineData("other/file.txt")]
    public void Import_RejectsUnsafePathsBeforeWritingAnything(string unsafePath)
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/good.txt", unsafePath);
        var destination = Path.Combine(directory.RootPath, "destination");
        var result = new WorkspaceArchiveService().Import(zip, destination, overwrite: true);
        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("ansight/file.txt", "ansight/file.txt")]
    [InlineData("ansight/file.txt", "ansight/FILE.txt")]
    [InlineData("ansight/Tasks/one.ts", "ansight/tasks/two.ts")]
    [InlineData("ansight/tasks", "ansight/tasks/one.ts")]
    [InlineData("ansight/tasks/one.ts", "ansight/tasks")]
    [InlineData("ansight/caf\u00e9.txt", "ansight/cafe\u0301.txt")]
    public void Import_RejectsDuplicateAndConflictingPaths(string first, string second)
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, first, second);
        var destination = Path.Combine(directory.RootPath, "destination");
        Assert.False(new WorkspaceArchiveService().Import(zip, destination, overwrite: true).IsSuccess);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(0xA1FF0000L)] // Unix symlink
    [InlineData(0x11FF0000L)] // Unix FIFO
    [InlineData(0x400L)] // Windows reparse point
    public void Import_RejectsArchiveLinksAndSpecialFiles(long attributes)
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/link");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            archive.Entries[0].ExternalAttributes = unchecked((int)attributes);
        var destination = Path.Combine(directory.RootPath, "destination");
        Assert.False(new WorkspaceArchiveService().Import(zip, destination).IsSuccess);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Import_RejectsCorruptPayloadBeforeReplacingAnyFiles()
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/good.txt", "ansight/bad.txt");
        var bytes = File.ReadAllBytes(zip);
        var offset = bytes.AsSpan().LastIndexOf(Encoding.UTF8.GetBytes("contents"));
        Assert.True(offset >= 0);
        bytes[offset] ^= 0x01;
        File.WriteAllBytes(zip, bytes);
        var destination = Path.Combine(directory.RootPath, "destination");
        Write(destination, "ansight/good.txt", [42]);

        var result = new WorkspaceArchiveService().Import(zip, destination, overwrite: true);

        Assert.False(result.IsSuccess);
        Assert.Contains("integrity", result.Message);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(destination, "ansight", "good.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "ansight", "bad.txt")));
    }

    [Fact]
    public void Import_RejectsOversizedEmptyAndMalformedArchives()
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/file.txt", "ansight/other.txt");
        var destination = Path.Combine(directory.RootPath, "destination");
        Assert.False(new WorkspaceArchiveService(1, 100).Import(zip, destination).IsSuccess);
        Assert.False(new WorkspaceArchiveService(100, 2).Import(zip, destination).IsSuccess);
        File.Delete(zip);
        CreateArchive(directory.RootPath);
        Assert.False(new WorkspaceArchiveService().Import(zip, destination).IsSuccess);
        File.WriteAllText(zip, "not a ZIP");
        Assert.False(new WorkspaceArchiveService().Import(zip, destination).IsSuccess);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void ImportAndExport_RejectFilesystemLinksIncludingDanglingLinks()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links can require elevation on Windows.
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.RootPath, "source");
        var destination = Path.Combine(directory.RootPath, "destination");
        var outside = Path.Combine(directory.RootPath, "outside");
        Directory.CreateDirectory(outside);
        Write(source, "ansight/file.txt", [42]);
        File.CreateSymbolicLink(Path.Combine(source, "ansight", "link.txt"), Path.Combine(outside, "missing.txt"));
        var service = new WorkspaceArchiveService();
        Assert.False(service.Export(source, Path.Combine(directory.RootPath, "export.zip")).IsSuccess);
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(Path.Combine(destination, "ansight"), outside);
        var zip = CreateArchive(directory.RootPath, "ansight/file.txt");
        Assert.False(service.Import(zip, destination, overwrite: true).IsSuccess);
        Assert.Empty(Directory.GetFiles(outside));
        Directory.Delete(Path.Combine(destination, "ansight"));
        Directory.CreateDirectory(Path.Combine(destination, "ansight"));
        File.CreateSymbolicLink(Path.Combine(destination, "ansight", "file.txt"), Path.Combine(outside, "missing.txt"));
        Assert.False(service.Import(zip, destination, overwrite: true).IsSuccess);
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void Export_RequiresWorkspaceAndExternalOutput_AndCancellationLeavesNoArchive()
    {
        using var directory = new TemporaryDirectory();
        var service = new WorkspaceArchiveService();
        var zip = Path.Combine(directory.RootPath, "workspace.zip");
        Assert.False(service.Export(directory.RootPath, zip).IsSuccess);
        Directory.CreateDirectory(Path.Combine(directory.RootPath, "ansight"));
        Assert.False(service.Export(directory.RootPath, Path.Combine(directory.RootPath, "ansight", "workspace.zip")).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Export(directory.RootPath, zip, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(zip));
        Assert.True(service.Export(directory.RootPath, zip).IsSuccess);
        Assert.True(service.Import(zip, Path.Combine(directory.RootPath, "empty")).IsSuccess);
        Assert.True(Directory.Exists(Path.Combine(directory.RootPath, "empty", "ansight")));
    }

    [Fact]
    public void ExportImport_PreservesUnixFilePermissionsWithoutSpecialBits()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        Write(directory.RootPath, "ansight/tools/run.sh", Encoding.UTF8.GetBytes("#!/bin/sh\nexit 0\n"));
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(Path.Combine(directory.RootPath, "ansight/tools/run.sh"), mode);
        var zip = Path.Combine(directory.RootPath, "workspace.zip");
        var destination = Path.Combine(directory.RootPath, "destination");
        var service = new WorkspaceArchiveService();
        Assert.True(service.Export(directory.RootPath, zip).IsSuccess);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            archive.GetEntry("ansight/tools/run.sh")!.ExternalAttributes |= 0x0E00 << 16;
        Assert.True(service.Import(zip, destination).IsSuccess);
        Assert.Equal(mode, File.GetUnixFileMode(Path.Combine(destination, "ansight/tools/run.sh")));
    }

    [Fact]
    public void Import_RollsBackFilesAndNewDirectoriesWhenMergeFails()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var zip = CreateArchive(directory.RootPath, "ansight/a.txt", "ansight/new/item.txt", "ansight/blocked/file.txt");
        var destination = Path.Combine(directory.RootPath, "destination");
        Write(destination, "ansight/a.txt", Encoding.UTF8.GetBytes("original"));
        var blocked = Path.Combine(destination, "ansight/blocked");
        Directory.CreateDirectory(blocked);
        var mode = File.GetUnixFileMode(blocked);
        try
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var result = new WorkspaceArchiveService().Import(zip, destination, overwrite: true);
            Assert.False(result.IsSuccess);
            Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "ansight/a.txt")));
            Assert.False(Directory.Exists(Path.Combine(destination, "ansight/new")));
            Assert.Empty(Directory.GetFiles(blocked));
        }
        finally { File.SetUnixFileMode(blocked, mode); }
    }

    [Fact]
    public void Export_RejectsOutputInsideWorkspaceThroughDirectoryAlias()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        Write(directory.RootPath, "ansight/file.txt", [42]);
        var alias = Path.Combine(directory.RootPath, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(directory.RootPath, "ansight"));
        var result = new WorkspaceArchiveService().Export(directory.RootPath, Path.Combine(alias, "workspace.zip"));
        Assert.False(result.IsSuccess);
        Assert.Contains("outside", result.Message);
        Assert.False(File.Exists(Path.Combine(alias, "workspace.zip")));
    }

    private static void Write(string root, string relativePath, byte[] contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static string CreateArchive(string root, params string[] paths)
    {
        var path = Path.Combine(root, "workspace.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entryPath in paths)
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            if (entryPath.EndsWith('/')) continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("contents");
        }
        return path;
    }
}
