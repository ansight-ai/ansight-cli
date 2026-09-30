namespace Ansight.Cli;

internal sealed class CliDataDirectoryLock : IDisposable
{
    private readonly FileStream stream;

    private CliDataDirectoryLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static CliDataDirectoryLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, "ansight.lock");
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new CliDataDirectoryLock(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliHostUnavailableException(
                $"Ansight data directory '{dataDirectory}' is already owned by another process or cannot be locked: {exception.Message}");
        }
    }

    public void Dispose()
    {
        stream.Dispose();
    }
}
