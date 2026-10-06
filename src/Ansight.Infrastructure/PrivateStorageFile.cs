namespace Ansight.Infrastructure;

public static class PrivateStorageFile
{
    public static FileStream Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
