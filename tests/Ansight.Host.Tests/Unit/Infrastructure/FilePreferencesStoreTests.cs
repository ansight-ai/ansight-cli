using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class FilePreferencesStoreTests
{
    [Fact]
    public void SetRecreatesParentDirectoryDeletedAfterStoreCreation()
    {
        var testRootPath = CreateTestRootPath();
        try
        {
            var dataPath = Path.Combine(testRootPath, "data");
            var preferencesPath = Path.Combine(dataPath, "ansight-preferences.json");
            var store = new FilePreferencesStore(preferencesPath);
            Directory.Delete(dataPath, recursive: true);

            store.Set("selected-device", "simulator-1");

            Assert.True(File.Exists(preferencesPath));
            Assert.Equal("simulator-1", store.Get("selected-device", string.Empty));
            Assert.Empty(Directory.EnumerateFiles(dataPath, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(testRootPath);
        }
    }

    [Fact]
    public void SetKeepsPreviousValueWhenPersistenceFails()
    {
        var testRootPath = CreateTestRootPath();
        try
        {
            var dataPath = Path.Combine(testRootPath, "data");
            var preferencesPath = Path.Combine(dataPath, "ansight-preferences.json");
            var store = new FilePreferencesStore(preferencesPath);
            store.Set("selected-device", "simulator-1");
            var changedCount = 0;
            store.Changed += (_, _) => changedCount++;

            Directory.Delete(dataPath, recursive: true);
            File.WriteAllText(dataPath, "blocks directory recreation");

            Assert.ThrowsAny<IOException>(() => store.Set("selected-device", "simulator-2"));
            Assert.Equal("simulator-1", store.Get("selected-device", string.Empty));
            Assert.Equal(0, changedCount);

            File.Delete(dataPath);
            store.Set("selected-device", "simulator-2");

            Assert.Equal("simulator-2", store.Get("selected-device", string.Empty));
            Assert.Equal(1, changedCount);
        }
        finally
        {
            TryDeleteDirectory(testRootPath);
        }
    }

    [Fact]
    public void RemoveKeepsValueWhenPersistenceFails()
    {
        var testRootPath = CreateTestRootPath();
        try
        {
            var dataPath = Path.Combine(testRootPath, "data");
            var preferencesPath = Path.Combine(dataPath, "ansight-preferences.json");
            var store = new FilePreferencesStore(preferencesPath);
            store.Set("selected-device", "simulator-1");
            var changedCount = 0;
            store.Changed += (_, _) => changedCount++;

            Directory.Delete(dataPath, recursive: true);
            File.WriteAllText(dataPath, "blocks directory recreation");

            Assert.ThrowsAny<IOException>(() => store.Remove("selected-device"));
            Assert.True(store.Contains("selected-device"));
            Assert.Equal("simulator-1", store.Get("selected-device", string.Empty));
            Assert.Equal(0, changedCount);

            File.Delete(dataPath);
            Assert.True(store.Remove("selected-device"));

            Assert.False(store.Contains("selected-device"));
            Assert.Equal(1, changedCount);
        }
        finally
        {
            TryDeleteDirectory(testRootPath);
        }
    }

    private static string CreateTestRootPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ansight-preference-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
