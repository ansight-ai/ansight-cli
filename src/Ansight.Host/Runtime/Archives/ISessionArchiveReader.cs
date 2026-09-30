using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Ansight.Host.Runtime.Archives;

internal interface ISessionArchiveReader : IDisposable
{
    IReadOnlyList<ISessionArchiveEntry> Entries { get; }
    bool HasEncryptedEntries { get; }
    ISessionArchiveEntry? GetEntry(string path);
}
