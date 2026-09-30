using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Ansight.Host.Runtime.Archives;

internal interface ISessionArchiveEntry
{
    string Name { get; }
    string FullName { get; }
    long Length { get; }
    Stream Open();
}
