using System.IO.Compression;

namespace Ansight.Host.Runtime.Archives;

internal sealed class SystemZipSessionArchiveReader : ISessionArchiveReader
{
    private readonly ZipArchive archive;
    private readonly bool ownsArchive;
    private readonly IReadOnlyList<ISessionArchiveEntry> entries;

    public SystemZipSessionArchiveReader(ZipArchive archive, bool ownsArchive = false)
    {
        this.archive = archive ?? throw new ArgumentNullException(nameof(archive));
        this.ownsArchive = ownsArchive;
        entries = archive.Entries
            .Select(entry => (ISessionArchiveEntry)new SystemZipSessionArchiveEntry(entry))
            .ToArray();
    }

    public IReadOnlyList<ISessionArchiveEntry> Entries => entries;
    public bool HasEncryptedEntries => false;

    public ISessionArchiveEntry? GetEntry(string path)
    {
        var entry = archive.GetEntry(path);
        return entry is null ? null : new SystemZipSessionArchiveEntry(entry);
    }

    public void Dispose()
    {
        if (ownsArchive)
        {
            archive.Dispose();
        }
    }

    private sealed class SystemZipSessionArchiveEntry(ZipArchiveEntry entry) : ISessionArchiveEntry
    {
        public string Name => entry.Name;
        public string FullName => entry.FullName;
        public long Length => entry.Length;

        public Stream Open()
        {
            return entry.Open();
        }
    }
}
