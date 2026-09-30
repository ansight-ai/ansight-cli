
namespace Ansight.Host;

public sealed class SessionArchiveVideoTransformResult : IDisposable
{
    private readonly string? ownedDirectoryPath;

    internal SessionArchiveVideoTransformResult(
        string archiveFilePath,
        bool containsVideo,
        string? ownedDirectoryPath)
    {
        ArchiveFilePath = archiveFilePath;
        ContainsVideo = containsVideo;
        this.ownedDirectoryPath = ownedDirectoryPath;
    }

    public string ArchiveFilePath { get; }

    public bool ContainsVideo { get; }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(ownedDirectoryPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(ownedDirectoryPath))
            {
                Directory.Delete(ownedDirectoryPath, recursive: true);
            }
        }
        catch
        {
            // Temporary transformed archives are best-effort cleanup only.
        }
    }
}
