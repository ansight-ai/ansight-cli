namespace Ansight.Host.Tests.TestSupport;

internal sealed class TestDirectory : IDisposable
{
    private TestDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TestDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ansight-host-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
