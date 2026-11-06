using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal interface ISessionArchiveSanitizer : IDisposable
{
    SessionSanitizedCapture Sanitize(AppSessionSnapshot snapshot);

    bool TryWriteScreenshot(
        string sourceFilePath,
        SessionImageFrame frame,
        ZipArchive archive,
        string entryName,
        IReadOnlyList<SessionSanitizationRegion> sensitiveRegions);

    IReadOnlySet<string> WriteArtifacts(
        string artifactsDirectoryPath,
        ZipArchive archive,
        Func<string, string> buildEntryName,
        IReadOnlyDictionary<string, string>? sanitizedRelativePaths = null);
}
