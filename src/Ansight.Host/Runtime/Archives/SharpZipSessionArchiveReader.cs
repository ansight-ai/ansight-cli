using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Ansight.Host.Runtime.Archives;

internal sealed class SharpZipSessionArchiveReader : ISessionArchiveReader
{
    private readonly SharpZipFile archive;
    private readonly IReadOnlyList<ISessionArchiveEntry> entries;

    public SharpZipSessionArchiveReader(string archiveFilePath, string? password)
    {
        archive = new SharpZipFile(archiveFilePath);
        if (!string.IsNullOrEmpty(password))
        {
            archive.Password = password;
        }

        entries = archive
            .Cast<ZipEntry>()
            .Select(entry => (ISessionArchiveEntry)new SharpZipSessionArchiveEntry(archive, entry))
            .ToArray();
    }

    public IReadOnlyList<ISessionArchiveEntry> Entries => entries;

    public bool HasEncryptedEntries => entries
        .OfType<SharpZipSessionArchiveEntry>()
        .Any(entry => entry.IsEncrypted);

    public ISessionArchiveEntry? GetEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var index = archive.FindEntry(path, ignoreCase: false);
        return index < 0
            ? null
            : new SharpZipSessionArchiveEntry(archive, archive[index]);
    }

    public void Dispose()
    {
        archive.Close();
    }

    private sealed class SharpZipSessionArchiveEntry(SharpZipFile archive, ZipEntry entry) : ISessionArchiveEntry
    {
        public string Name => entry.IsDirectory ? string.Empty : Path.GetFileName(entry.Name);
        public string FullName => entry.Name;
        public long Length => entry.Size;
        public bool IsEncrypted => entry.IsCrypted;

        public Stream Open()
        {
            return archive.GetInputStream(entry);
        }
    }
}
