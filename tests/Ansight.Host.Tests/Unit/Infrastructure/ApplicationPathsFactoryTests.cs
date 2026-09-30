namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class ApplicationPathsFactoryTests
{
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
