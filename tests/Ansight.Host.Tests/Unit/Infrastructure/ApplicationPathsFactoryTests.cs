namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class ApplicationPathsFactoryTests
{
    [Fact]
    public void ApplicationPaths_MigratesLegacyAppFilesWithoutOverwritingExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ansight-path-tests-{Guid.NewGuid():N}");
        var dataPath = Path.Combine(root, "data");
        var appsPath = Path.Combine(dataPath, "apps");
        try
        {
            Directory.CreateDirectory(appsPath);
            File.WriteAllText(Path.Combine(dataPath, "first.ans.json"), "first");
            File.WriteAllText(Path.Combine(dataPath, "existing.ans.json"), "older");
            File.WriteAllText(Path.Combine(appsPath, "existing.ans.json"), "newer");
            File.WriteAllText(Path.Combine(dataPath, "known-apps.json"), "catalog");

            _ = new DataToolApplicationPaths(root);
            _ = new DataToolApplicationPaths(root);

            if (!OperatingSystem.IsWindows())
            {
                var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Assert.Equal(privateMode, File.GetUnixFileMode(root));
                Assert.Equal(privateMode, File.GetUnixFileMode(dataPath));
                Assert.Equal(privateMode, File.GetUnixFileMode(appsPath));
            }

            Assert.Equal("first", File.ReadAllText(Path.Combine(appsPath, "first.ans.json")));
            Assert.False(File.Exists(Path.Combine(dataPath, "first.ans.json")));
            Assert.Equal("newer", File.ReadAllText(Path.Combine(appsPath, "existing.ans.json")));
            Assert.Equal("older", File.ReadAllText(Path.Combine(dataPath, "existing.ans.json")));
            Assert.Equal("catalog", File.ReadAllText(Path.Combine(dataPath, "known-apps.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Cli")]
    [InlineData("Studio")]
    public void MigratesPreviousInstallFolderWithoutRemovingIt(string folderName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ansight-path-tests-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "Ansight");
        var previous = Path.Combine(current, folderName);
        try
        {
            Directory.CreateDirectory(Path.Combine(previous, "data"));
            File.WriteAllText(Path.Combine(previous, "data", "settings.json"), "saved");

            ApplicationPathsFactory.TryMigrateLegacyBaseFolders(current, previous);

            Assert.Equal("saved", File.ReadAllText(Path.Combine(current, "data", "settings.json")));
            Assert.True(File.Exists(Path.Combine(previous, "data", "settings.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MigratesSecureStorageAndKeyAsOneBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ansight-path-tests-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "Ansight");
        var previous = Path.Combine(current, "Cli", "data");
        try
        {
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "secure-storage.json"), "storage");
            File.WriteAllText(Path.Combine(previous, "secure-storage.key"), "key");

            ApplicationPathsFactory.TryMigrateLegacyBaseFolders(current, Path.GetDirectoryName(previous)!);

            Assert.Equal("storage", File.ReadAllText(Path.Combine(current, "data", "secure-storage.json")));
            Assert.Equal("key", File.ReadAllText(Path.Combine(current, "data", "secure-storage.key")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
