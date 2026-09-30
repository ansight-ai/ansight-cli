using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Ansight.Host.Runtime.Operations;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Files;

public sealed partial class FileVisualizationService
{
    private const int DefaultPreviewBytes = 10 * 1024 * 1024;
    private const int LiveFileDownloadChunkBytes = 1024 * 1024;
    private const int MaximumDatabaseRows = 500;
    private static readonly byte[] sqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly HashSet<string> codeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cs", ".css", ".dart", ".fs", ".go", ".gradle", ".h", ".hpp",
        ".html", ".java", ".js", ".jsx", ".kt", ".kts", ".lua", ".m", ".mm", ".php", ".py",
        ".rb", ".razor", ".rs", ".sh", ".sql", ".swift", ".toml", ".ts", ".tsx", ".vue",
        ".yaml", ".yml"
    };
    private static readonly HashSet<string> imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".heif", ".ico", ".jpeg", ".jpg", ".png", ".svg", ".webp"
    };
    private static readonly IReadOnlyDictionary<string, string> mediaMimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".3gp"] = "video/3gpp",
        [".3g2"] = "video/3gpp2",
        [".avi"] = "video/x-msvideo",
        [".m4v"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".mov"] = "video/quicktime",
        [".mp4"] = "video/mp4",
        [".mpe"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".mpg"] = "video/mpeg",
        [".ogv"] = "video/ogg",
        [".webm"] = "video/webm",
        [".aac"] = "audio/aac",
        [".aif"] = "audio/aiff",
        [".aiff"] = "audio/aiff",
        [".caf"] = "audio/x-caf",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".m4b"] = "audio/mp4",
        [".mp2"] = "audio/mpeg",
        [".mp3"] = "audio/mpeg",
        [".oga"] = "audio/ogg",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".weba"] = "audio/webm"
    };
    private static readonly HashSet<string> sqliteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".db3", ".sqlite", ".sqlite3"
    };
    private static readonly JsonSerializerOptions prettyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Lazy<bool> sqliteProviderInitialization = new(InitializeSqliteProvider);
    private readonly RuntimeCoordinator? runtime;

    public FileVisualizationService()
    {
    }

    internal FileVisualizationService(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    private RuntimeCoordinator Runtime => runtime
        ?? throw new InvalidOperationException(
            "This file visualization service was created for local-file inspection only.");

    public async Task<FileVisualization> InspectLocalFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var normalizedPath = Path.GetFullPath(filePath);
        if (!File.Exists(normalizedPath))
        {
            return FileVisualization.Failure($"File '{normalizedPath}' was not found.");
        }

        return await InspectLocalFileCoreAsync(normalizedPath, null, null, false, false, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<FileVisualization> InspectLiveFileAsync(
        string sessionId,
        string? sandboxRoot,
        string sandboxPath,
        CancellationToken cancellationToken = default)
        => InspectLiveFileAsync(sessionId, sandboxRoot, sandboxPath, false, false, cancellationToken);

    public Task<FileVisualization> InspectLiveFileAsync(
        string sessionId,
        string? sandboxRoot,
        string sandboxPath,
        bool forceText,
        CancellationToken cancellationToken = default)
        => InspectLiveFileAsync(sessionId, sandboxRoot, sandboxPath, forceText, false, cancellationToken);

    public async Task<FileVisualization> InspectLiveFileAsync(
        string sessionId,
        string? sandboxRoot,
        string sandboxPath,
        bool forceText,
        bool allowLargeFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);

        if (runtime?.LiveFiles.IsExternal(sessionId) == true)
            return await InspectExternalFileAsync(sessionId, sandboxRoot, sandboxPath, forceText, allowLargeFile, cancellationToken).ConfigureAwait(false);

        var normalizedPath = sandboxPath.Trim();
        var extension = Path.GetExtension(normalizedPath).ToLowerInvariant();
        var viewerKind = forceText
            ? FileViewerKinds.Text
            : Classify(extension, null);
        if (IsStreamingViewer(viewerKind))
        {
            return new FileVisualization(
                true,
                "File stream ready.",
                viewerKind,
                Path.GetFileName(normalizedPath),
                extension,
                ResolveMimeType(extension),
                ResolveLanguage(viewerKind, extension),
                0,
                0,
                false,
                null,
                null,
                null);
        }
        if (viewerKind == FileViewerKinds.Sqlite)
        {
            return await InspectLiveDatabaseAsync(sessionId.Trim(), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var download = await DownloadLiveFilePreviewAsync(
                sessionId.Trim(),
                sandboxRoot,
                normalizedPath,
                allowLargeFile ? null : DefaultPreviewBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return FileVisualization.Failure(download.Message);
        }
        var result = download.Result;

        var fileName = ReadString(result, "fileName") ?? Path.GetFileName(normalizedPath);
        extension = NormalizeExtension(ReadString(result, "fileExtension"), fileName);
        var mimeType = ResolveMimeType(extension, ReadString(result, "mimeType") ?? ReadString(result, "contentType"));
        viewerKind = forceText
            ? FileViewerKinds.Text
            : Classify(extension, mimeType);
        var text = ReadString(result, "text");
        var base64 = ReadString(result, "base64");
        string? formatLabel = null;
        FileStructuredData? structuredData = null;
        if (!forceText && extension == ".plist" && (base64 is not null || text is not null))
        {
            try
            {
                var propertyListBytes = base64 is not null
                    ? Convert.FromBase64String(base64)
                    : Encoding.UTF8.GetBytes(text!);
                var propertyList = PropertyListParser.Parse(propertyListBytes);
                text = propertyList.SourceText;
                base64 = null;
                formatLabel = propertyList.FormatLabel;
                structuredData = propertyList.StructuredData;
            }
            catch (Exception exception) when (exception is FormatException or InvalidDataException)
            {
                return FileVisualization.Failure(
                    $"The property list could not be previewed. {exception.Message}");
            }
        }

        if (text is null && base64 is not null && IsTextViewer(viewerKind))
        {
            text = TryDecodeUtf8(base64);
            if (text is not null)
            {
                base64 = null;
            }
        }

        if (!forceText
            && viewerKind != FileViewerKinds.Sqlite
            && base64 is not null
            && HasSqliteHeader(base64))
        {
            return await InspectLiveDatabaseAsync(sessionId.Trim(), normalizedPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var rawText = text;
        if (text is not null)
        {
            text = FormatText(viewerKind, text);
        }

        return new FileVisualization(
            true,
            "File preview ready.",
            viewerKind,
            fileName,
            extension,
            mimeType,
            ResolveLanguage(viewerKind, extension),
            ReadInt64(result, "sizeBytes"),
            ReadInt64(result, "bytesRead"),
            ReadBoolean(result, "truncated"),
            text,
            base64,
            null,
            rawText,
            formatLabel,
            structuredData);
    }

    public Task<FileVisualization> InspectSessionArtifactAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        CancellationToken cancellationToken = default)
        => InspectSessionArtifactAsync(sessionId, snapshotId, artifactPath, false, false, cancellationToken);

    public Task<FileVisualization> InspectSessionArtifactAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        bool forceText,
        CancellationToken cancellationToken = default)
        => InspectSessionArtifactAsync(sessionId, snapshotId, artifactPath, forceText, false, cancellationToken);

    public async Task<FileVisualization> InspectSessionArtifactAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        bool forceText,
        bool allowLargeFile,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveSessionArtifactAsync(
                sessionId,
                snapshotId,
                artifactPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsSuccess || resolution.FilePath is null || resolution.Entry is null)
        {
            return FileVisualization.Failure(resolution.Message);
        }

        var extension = NormalizeExtension(
            resolution.Entry.FileExtension,
            resolution.Entry.Name ?? Path.GetFileName(resolution.FilePath));
        var mimeType = ResolveMimeType(extension, resolution.Entry.MimeType);
        var viewerKind = forceText
            ? FileViewerKinds.Text
            : Classify(extension, mimeType);
        var artifactDetails = await CalculateArtifactDetailsAsync(
                resolution.FilePath,
                resolution.Entry.LastModifiedUtc,
                cancellationToken)
            .ConfigureAwait(false);
        if (IsStreamingViewer(viewerKind))
        {
            var file = new FileInfo(resolution.FilePath);
            return new FileVisualization(
                true,
                "File stream ready.",
                viewerKind,
                resolution.Entry.Name ?? file.Name,
                extension,
                mimeType,
                ResolveLanguage(viewerKind, extension),
                file.Length,
                0,
                false,
                null,
                null,
                null,
                ArtifactDetails: artifactDetails);
        }

        var preview = await InspectLocalFileCoreAsync(
                resolution.FilePath,
                resolution.Entry.Name,
                resolution.Entry.MimeType,
                forceText,
                allowLargeFile,
                cancellationToken)
            .ConfigureAwait(false);
        return preview with { ArtifactDetails = artifactDetails };
    }

    public async Task<FileContentSource> ResolveSessionArtifactContentAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveSessionArtifactAsync(
                sessionId,
                snapshotId,
                artifactPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsSuccess || resolution.FilePath is null || resolution.Entry is null)
        {
            return FileContentSource.Failure(resolution.Message);
        }

        var file = new FileInfo(resolution.FilePath);
        var extension = NormalizeExtension(resolution.Entry.FileExtension, resolution.Entry.Name ?? file.Name);
        var mimeType = ResolveMimeType(extension, resolution.Entry.MimeType);
        return new FileContentSource(
            true,
            "Artifact content ready.",
            file.FullName,
            resolution.Entry.Name ?? file.Name,
            mimeType,
            file.Length);
    }

    public async Task<FileDatabaseQueryResult> QueryLocalDatabaseAsync(
        string filePath,
        string sql,
        int maximumRows = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!TryValidateReadOnlySql(sql, out var normalizedSql, out var validationMessage))
        {
            return FileDatabaseQueryResult.Failure(validationMessage);
        }

        try
        {
            EnsureSqliteProviderInitialized();
            await using var connection = await OpenReadOnlyDatabaseAsync(Path.GetFullPath(filePath), cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = normalizedSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLocalQueryResultAsync(
                    reader,
                    normalizedSql,
                    Math.Clamp(maximumRows, 1, MaximumDatabaseRows),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FileDatabaseQueryResult.Failure(exception.Message);
        }
    }

    public async Task<FileDatabaseQueryResult> QueryLiveDatabaseAsync(
        string sessionId,
        string sandboxPath,
        string sql,
        int maximumRows = 100,
        CancellationToken cancellationToken = default,
        string? sandboxRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);
        if (!TryValidateReadOnlySql(sql, out var normalizedSql, out var validationMessage))
        {
            return FileDatabaseQueryResult.Failure(validationMessage);
        }

        if (runtime?.LiveFiles.IsExternal(sessionId) == true)
        {
            try
            {
                using var copy = await Runtime.LiveFiles.CopyAsync(sessionId, sandboxRoot, sandboxPath, true, cancellationToken).ConfigureAwait(false);
                return await QueryLocalDatabaseAsync(copy.Path, normalizedSql, maximumRows, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return FileDatabaseQueryResult.Failure(exception.Message);
            }
        }

        var arguments = new JsonObject
        {
            ["path"] = sandboxPath.Trim(),
            ["sql"] = normalizedSql,
            ["maxRows"] = Math.Clamp(maximumRows, 1, MaximumDatabaseRows)
        };
        var response = await Runtime.AppTools.CallWithCatalogAsync(
                sessionId.Trim(),
                "data.query",
                arguments,
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryReadToolResult(response, out var result, out var errorMessage))
        {
            return FileDatabaseQueryResult.Failure(errorMessage);
        }

        return CreateRemoteQueryResult(result, normalizedSql);
    }

    public async Task<FileDatabaseQueryResult> QuerySessionArtifactDatabaseAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        string sql,
        int maximumRows = 100,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveSessionArtifactAsync(
                sessionId,
                snapshotId,
                artifactPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.IsSuccess || resolution.FilePath is null)
        {
            return FileDatabaseQueryResult.Failure(resolution.Message);
        }

        return await QueryLocalDatabaseAsync(
                resolution.FilePath,
                sql,
                maximumRows,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string Classify(string? extension, string? mimeType)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
        var normalizedMimeType = mimeType?.Trim().ToLowerInvariant() ?? string.Empty;

        if (sqliteExtensions.Contains(normalizedExtension)
            || normalizedMimeType.Contains("sqlite", StringComparison.Ordinal))
        {
            return FileViewerKinds.Sqlite;
        }

        if (normalizedExtension is ".json" or ".geojson" or ".har"
            || normalizedMimeType.Contains("json", StringComparison.Ordinal))
        {
            return FileViewerKinds.Json;
        }

        if (normalizedExtension is ".xml" or ".xaml" or ".plist" or ".csproj" or ".props" or ".targets"
            || normalizedMimeType is "application/xml" or "text/xml")
        {
            return FileViewerKinds.Xml;
        }

        if (normalizedExtension is ".md" or ".markdown" or ".mdx")
        {
            return FileViewerKinds.Markdown;
        }

        if (normalizedExtension == ".csv" || normalizedMimeType == "text/csv")
        {
            return FileViewerKinds.Csv;
        }

        if (normalizedExtension is ".glb" or ".gltf" or ".obj" or ".stl")
        {
            return FileViewerKinds.Model3D;
        }

        if (normalizedExtension == ".pdf" || normalizedMimeType == "application/pdf")
        {
            return FileViewerKinds.Pdf;
        }

        if (imageExtensions.Contains(normalizedExtension) || normalizedMimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            return FileViewerKinds.Image;
        }

        if (normalizedMimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            return FileViewerKinds.Audio;
        }

        if (normalizedMimeType.StartsWith("video/", StringComparison.Ordinal))
        {
            return FileViewerKinds.Video;
        }

        if (mediaMimeTypes.TryGetValue(normalizedExtension, out var mediaMimeType))
        {
            return mediaMimeType.StartsWith("audio/", StringComparison.Ordinal)
                ? FileViewerKinds.Audio
                : FileViewerKinds.Video;
        }

        if (codeExtensions.Contains(normalizedExtension))
        {
            return FileViewerKinds.Code;
        }

        return normalizedMimeType.StartsWith("text/", StringComparison.Ordinal)
               || normalizedExtension is ".ini" or ".log" or ".txt"
            ? FileViewerKinds.Text
            : FileViewerKinds.Binary;
    }

    internal static async Task<FileArtifactDetails> CalculateArtifactDetailsAsync(
        string filePath,
        string? downloadedAtUtc,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[bufferSize];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            useAsync: true);

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha256.AppendData(buffer, 0, bytesRead);
            sha1.AppendData(buffer, 0, bytesRead);
            md5.AppendData(buffer, 0, bytesRead);
        }

        var resolvedDownloadedAtUtc = string.IsNullOrWhiteSpace(downloadedAtUtc)
            ? new FileInfo(filePath).LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)
            : downloadedAtUtc.Trim();
        return new FileArtifactDetails(
            resolvedDownloadedAtUtc,
            Convert.ToHexStringLower(sha256.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(md5.GetHashAndReset()));
    }

    private async Task<FileVisualization> InspectLocalFileCoreAsync(
        string filePath,
        string? displayName,
        string? suppliedMimeType,
        bool forceText,
        bool allowLargeFile,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(filePath);
            var extension = file.Extension.ToLowerInvariant();
            var mimeType = ResolveMimeType(extension, suppliedMimeType);
            var viewerKind = forceText
                ? FileViewerKinds.Text
                : Classify(extension, mimeType);
            if (viewerKind is FileViewerKinds.Audio or FileViewerKinds.Video)
            {
                return new FileVisualization(
                    true,
                    "File stream ready.",
                    viewerKind,
                    displayName ?? file.Name,
                    extension,
                    mimeType,
                    ResolveLanguage(viewerKind, extension),
                    file.Length,
                    0,
                    false,
                    null,
                    null,
                    null);
            }
            if (!forceText
                && viewerKind != FileViewerKinds.Sqlite
                && file.Length >= sqliteHeader.Length
                && await IsSqliteFileAsync(filePath, cancellationToken).ConfigureAwait(false))
            {
                viewerKind = FileViewerKinds.Sqlite;
                mimeType = "application/vnd.sqlite3";
            }

            if (viewerKind == FileViewerKinds.Sqlite)
            {
                var schema = await LoadLocalDatabaseSchemaAsync(filePath, cancellationToken).ConfigureAwait(false);
                return new FileVisualization(
                    true,
                    "SQLite schema ready.",
                    viewerKind,
                    displayName ?? file.Name,
                    extension,
                    mimeType,
                    "SQLite",
                    file.Length,
                    0,
                    false,
                    null,
                    null,
                    schema);
            }

            if (allowLargeFile && file.Length > int.MaxValue)
            {
                return FileVisualization.Failure(
                    $"The {file.Length:N0}-byte file is too large to preview in memory.");
            }

            var maximumBytes = allowLargeFile
                ? checked((int)file.Length)
                : DefaultPreviewBytes;
            var bytes = await ReadLimitedBytesAsync(filePath, maximumBytes, cancellationToken).ConfigureAwait(false);
            var isTruncated = file.Length > bytes.Length;
            string? text = null;
            string? base64 = null;
            string? rawText = null;
            string? formatLabel = null;
            FileStructuredData? structuredData = null;
            if (IsTextViewer(viewerKind))
            {
                if (!forceText && extension == ".plist")
                {
                    var propertyList = PropertyListParser.Parse(bytes);
                    rawText = propertyList.SourceText;
                    formatLabel = propertyList.FormatLabel;
                    structuredData = propertyList.StructuredData;
                }
                else
                {
                    rawText = DecodeText(bytes);
                }
                text = FormatText(viewerKind, rawText);
            }
            else
            {
                base64 = Convert.ToBase64String(bytes);
            }

            return new FileVisualization(
                true,
                "File preview ready.",
                viewerKind,
                displayName ?? file.Name,
                extension,
                mimeType,
                ResolveLanguage(viewerKind, extension),
                file.Length,
                bytes.Length,
                isTruncated,
                text,
                base64,
                null,
                rawText,
                formatLabel,
                structuredData);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or SqliteException
                                           or XmlException
                                           or JsonException)
        {
            return FileVisualization.Failure(exception.Message);
        }
    }

    private async Task<FileVisualization> InspectLiveDatabaseAsync(
        string sessionId,
        string sandboxPath,
        CancellationToken cancellationToken)
    {
        var response = await Runtime.AppTools.CallWithCatalogAsync(
                sessionId,
                "data.describe_schema",
                new JsonObject { ["path"] = sandboxPath },
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryReadToolResult(response, out var result, out var errorMessage))
        {
            return FileVisualization.Failure(
                $"SQLite preview requires the app's data.describe_schema tool. {errorMessage}");
        }

        var schema = CreateRemoteDatabaseSchema(result, sandboxPath);
        return new FileVisualization(
            true,
            "SQLite schema ready.",
            FileViewerKinds.Sqlite,
            Path.GetFileName(sandboxPath),
            Path.GetExtension(sandboxPath).ToLowerInvariant(),
            "application/vnd.sqlite3",
            "SQLite",
            0,
            0,
            false,
            null,
            null,
            schema);
    }

    private async Task<ResolvedArtifactFile> ResolveSessionArtifactAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(artifactPath))
        {
            return ResolvedArtifactFile.Failure("Session ID and artifact path are required.");
        }

        var session = await Runtime.Sessions.LoadSnapshotAsync(sessionId.Trim(), null, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return ResolvedArtifactFile.Failure($"Session '{sessionId}' was not found.");
        }

        var requestedPath = SessionFileLocator.NormalizeArtifactPath(artifactPath);
        var normalizedSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? null : snapshotId.Trim();
        foreach (var snapshot in session.ArtifactSnapshots.Where(candidate =>
                     normalizedSnapshotId is null
                     || string.Equals(candidate.SnapshotId, normalizedSnapshotId, StringComparison.Ordinal)))
        {
            var entry = snapshot.Entries.FirstOrDefault(candidate =>
                string.Equals(
                    SessionFileLocator.NormalizeArtifactPath(candidate.SnapshotRelativePath),
                    requestedPath,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    SessionFileLocator.NormalizeArtifactPath(candidate.ArchiveRelativePath),
                    requestedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            if (string.Equals(entry.Kind, "directory", StringComparison.OrdinalIgnoreCase))
            {
                return ResolvedArtifactFile.Failure($"Artifact path '{requestedPath}' is a directory.");
            }

            if (!SessionFileLocator.TryResolveArtifactEntryPath(
                    Runtime.ApplicationPaths,
                    session,
                    snapshot,
                    entry,
                    out var filePath)
                || !File.Exists(filePath))
            {
                return ResolvedArtifactFile.Failure(
                    $"Artifact file '{requestedPath}' metadata exists, but the local file was not found.");
            }

            return ResolvedArtifactFile.Success(filePath, entry);
        }

        return ResolvedArtifactFile.Failure($"Artifact file '{requestedPath}' was not found.");
    }

    private static async Task<FileDatabaseSchema> LoadLocalDatabaseSchemaAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        EnsureSqliteProviderInitialized();
        await using var connection = await OpenReadOnlyDatabaseAsync(filePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type, COALESCE(sql, '')
            FROM sqlite_schema
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name COLLATE NOCASE
            """;
        var objects = new List<FileDatabaseObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new List<DatabaseObjectDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(new DatabaseObjectDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        foreach (var definition in definitions)
        {
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = """
                SELECT name, type, "notnull", dflt_value, pk
                FROM pragma_table_xinfo($objectName)
                WHERE hidden = 0
                ORDER BY cid
                """;
            columnsCommand.Parameters.AddWithValue("$objectName", definition.Name);
            var columns = new List<FileDatabaseColumn>();
            await using var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await columnsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = columnsReader.GetString(0);
                columns.Add(new FileDatabaseColumn(
                    name,
                    name,
                    columnsReader.IsDBNull(1) ? string.Empty : columnsReader.GetString(1),
                    columnsReader.GetInt64(2) == 0,
                    columnsReader.GetInt64(4) > 0,
                    columnsReader.IsDBNull(3) ? string.Empty : columnsReader.GetValue(3).ToString() ?? string.Empty));
            }

            objects.Add(new FileDatabaseObject(
                definition.Name,
                definition.Type,
                definition.Sql,
                columns));
        }

        return new FileDatabaseSchema(filePath, objects);
    }

    private static FileDatabaseSchema CreateRemoteDatabaseSchema(JsonObject result, string fallbackPath)
    {
        var objects = new List<FileDatabaseObject>();
        if (result["tables"] is JsonArray tables)
        {
            foreach (var table in tables.OfType<JsonObject>())
            {
                var name = ReadString(table, "name") ?? string.Empty;
                var columns = new List<FileDatabaseColumn>();
                if (table["columns"] is JsonArray columnNodes)
                {
                    foreach (var column in columnNodes.OfType<JsonObject>())
                    {
                        var columnName = ReadString(column, "name") ?? string.Empty;
                        columns.Add(new FileDatabaseColumn(
                            columnName,
                            columnName,
                            ReadString(column, "type") ?? string.Empty,
                            ReadInt64(column, "notnull") == 0,
                            ReadInt64(column, "pk") > 0,
                            FormatJsonValue(column["dflt_value"])));
                    }
                }

                objects.Add(new FileDatabaseObject(
                    name,
                    ReadString(table, "type") ?? "table",
                    ReadString(table, "sql") ?? string.Empty,
                    columns));
            }
        }

        return new FileDatabaseSchema(
            ReadString(result, "databasePath") ?? fallbackPath,
            objects);
    }

    private static async Task<FileDatabaseQueryResult> ReadLocalQueryResultAsync(
        SqliteDataReader reader,
        string sql,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(index => new FileDatabaseColumn(
                reader.GetName(index),
                CreateColumnKey(reader.GetName(index), index),
                reader.GetDataTypeName(index),
                true,
                false,
                string.Empty))
            .ToArray();
        var rows = new List<IReadOnlyList<string>>();
        while (rows.Count <= maximumRows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cells = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                cells[index] = FormatDatabaseValue(reader.GetValue(index));
            }
            rows.Add(cells);
        }

        var isTruncated = rows.Count > maximumRows;
        if (isTruncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new FileDatabaseQueryResult(
            true,
            isTruncated ? $"Showing the first {maximumRows:N0} rows." : $"Returned {rows.Count:N0} rows.",
            sql,
            columns,
            rows,
            isTruncated);
    }

    private static FileDatabaseQueryResult CreateRemoteQueryResult(JsonObject result, string sql)
    {
        var columns = new List<FileDatabaseColumn>();
        if (result["columnMetadata"] is JsonArray columnMetadata)
        {
            foreach (var column in columnMetadata.OfType<JsonObject>())
            {
                var name = ReadString(column, "name") ?? string.Empty;
                columns.Add(new FileDatabaseColumn(
                    name,
                    ReadString(column, "key") ?? name,
                    ReadString(column, "declaredType") ?? string.Empty,
                    true,
                    false,
                    string.Empty));
            }
        }
        else if (result["columns"] is JsonArray columnNames)
        {
            columns.AddRange(columnNames.Select((column, index) =>
            {
                var name = column?.GetValue<string>() ?? string.Empty;
                return new FileDatabaseColumn(name, CreateColumnKey(name, index), string.Empty, true, false, string.Empty);
            }));
        }

        var rows = new List<IReadOnlyList<string>>();
        if (result["rowValues"] is JsonArray rowValues)
        {
            foreach (var row in rowValues.OfType<JsonArray>())
            {
                rows.Add(row
                    .Select(cell => cell is JsonObject cellObject
                        ? FormatJsonValue(cellObject["value"])
                        : FormatJsonValue(cell))
                    .ToArray());
            }
        }
        else if (result["rows"] is JsonArray rowObjects)
        {
            foreach (var row in rowObjects.OfType<JsonObject>())
            {
                rows.Add(columns.Select(column => FormatJsonValue(row[column.Key])).ToArray());
            }
        }

        var isTruncated = ReadBoolean(result, "truncated");
        return new FileDatabaseQueryResult(
            true,
            isTruncated ? $"Showing the first {rows.Count:N0} rows." : $"Returned {rows.Count:N0} rows.",
            ReadString(result, "sql") ?? sql,
            columns,
            rows,
            isTruncated);
    }

    private static bool TryReadToolResult(
        RuntimeAppToolResponse response,
        out JsonObject result,
        out string errorMessage)
    {
        result = new JsonObject();
        errorMessage = response.Message;
        if (!response.Success || response.Envelope?.Payload is not JsonObject payload)
        {
            return false;
        }

        if (payload["success"] is JsonValue successValue
            && successValue.TryGetValue<bool>(out var success)
            && !success)
        {
            errorMessage = ReadString(payload, "message") ?? response.Message;
            return false;
        }

        if (payload["result"] is not JsonObject resultObject)
        {
            errorMessage = ReadString(payload, "message") ?? "The app returned no file result.";
            return false;
        }

        result = resultObject;
        return true;
    }

    private async Task<LiveFilePreviewDownload> DownloadLiveFilePreviewAsync(
        string sessionId,
        string? sandboxRoot,
        string sandboxPath,
        int? maximumBytes,
        CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        JsonObject? firstResult = null;
        string? expectedVersion = null;
        var hasMore = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingBytes = maximumBytes.HasValue
                ? maximumBytes.Value - content.Length
                : LiveFileDownloadChunkBytes;
            if (remainingBytes <= 0)
            {
                hasMore = true;
                break;
            }

            var arguments = new JsonObject
            {
                ["path"] = sandboxPath,
                ["offsetBytes"] = content.Length,
                ["maxBytes"] = (int)Math.Min(LiveFileDownloadChunkBytes, remainingBytes),
                ["encoding"] = "base64"
            };
            if (!string.IsNullOrWhiteSpace(sandboxRoot))
            {
                arguments["root"] = sandboxRoot.Trim();
            }
            if (!string.IsNullOrWhiteSpace(expectedVersion))
            {
                arguments["expectedVersion"] = expectedVersion;
            }

            var response = await Runtime.LiveFiles.CallAsync(
                    sessionId,
                    "files.download_file",
                    arguments,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryReadToolResult(response, out var result, out var errorMessage))
            {
                return LiveFilePreviewDownload.Failure(errorMessage);
            }

            firstResult ??= result.DeepClone().AsObject();
            if (!maximumBytes.HasValue && ReadInt64(result, "sizeBytes") > int.MaxValue)
            {
                return LiveFilePreviewDownload.Failure(
                    $"The {ReadInt64(result, "sizeBytes"):N0}-byte file is too large to preview in memory.");
            }
            var base64 = ReadString(result, "base64");
            if (base64 is null)
            {
                return LiveFilePreviewDownload.Failure(
                    "The app returned no binary data for the file preview.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return LiveFilePreviewDownload.Failure(
                    "The app returned invalid base64 data for the file preview.");
            }

            await content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            hasMore = ReadBoolean(result, "hasMore");
            if (!hasMore)
            {
                break;
            }
            if (bytes.Length == 0)
            {
                return LiveFilePreviewDownload.Failure(
                    "The app returned an empty file chunk before the preview was complete.");
            }

            expectedVersion ??= ReadString(result, "version");
        }

        if (firstResult is null)
        {
            return LiveFilePreviewDownload.Failure("The app returned no file preview.");
        }

        firstResult["bytesRead"] = content.Length;
        firstResult["truncated"] = hasMore;
        firstResult["encoding"] = "base64";
        firstResult["text"] = null;
        firstResult["base64"] = Convert.ToBase64String(content.GetBuffer(), 0, checked((int)content.Length));
        return LiveFilePreviewDownload.Success(firstResult);
    }

    private static bool TryValidateReadOnlySql(
        string sql,
        out string normalizedSql,
        out string message)
    {
        normalizedSql = sql?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            message = "A read-only SQL query is required.";
            return false;
        }

        var firstToken = normalizedSql
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant();
        if (firstToken is not ("select" or "with" or "pragma" or "explain"))
        {
            message = "The local player only permits read-only SELECT, WITH, PRAGMA, or EXPLAIN queries.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static async Task<SqliteConnection> OpenReadOnlyDatabaseAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> IsSqliteFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var bytes = await ReadLimitedBytesAsync(filePath, sqliteHeader.Length, cancellationToken).ConfigureAwait(false);
        return bytes.AsSpan().SequenceEqual(sqliteHeader);
    }

    private static bool HasSqliteHeader(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.AsSpan().StartsWith(sqliteHeader);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        string filePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var byteCount = checked((int)Math.Min(stream.Length, maximumBytes));
        var bytes = new byte[byteCount];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            offset += count;
        }

        return offset == bytes.Length ? bytes : bytes[..offset];
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static string? TryDecodeUtf8(string base64)
    {
        try
        {
            return DecodeText(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string FormatText(string viewerKind, string text)
    {
        try
        {
            if (viewerKind == FileViewerKinds.Json)
            {
                return JsonNode.Parse(text)?.ToJsonString(prettyJsonOptions) ?? text;
            }

            if (viewerKind == FileViewerKinds.Xml)
            {
                using var stringReader = new StringReader(text);
                using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                return XDocument.Load(xmlReader).ToString();
            }
        }
        catch (Exception exception) when (exception is JsonException or XmlException)
        {
            return text;
        }

        return text;
    }

    private static bool IsTextViewer(string viewerKind)
        => viewerKind is FileViewerKinds.Code
            or FileViewerKinds.Csv
            or FileViewerKinds.Json
            or FileViewerKinds.Markdown
            or FileViewerKinds.Text
            or FileViewerKinds.Xml;

    private static bool IsStreamingViewer(string viewerKind)
        => viewerKind is FileViewerKinds.Model3D or FileViewerKinds.Audio or FileViewerKinds.Video;

    private static string ResolveLanguage(string viewerKind, string extension)
    {
        if (extension == ".plist")
        {
            return "Property List";
        }

        if (viewerKind != FileViewerKinds.Code)
        {
            return viewerKind switch
            {
                FileViewerKinds.Csv => "CSV",
                FileViewerKinds.Json => "JSON",
                FileViewerKinds.Markdown => "Markdown",
                FileViewerKinds.Xml => "XML",
                FileViewerKinds.Text => "Text",
                FileViewerKinds.Model3D => "3D asset",
                FileViewerKinds.Pdf => "PDF",
                FileViewerKinds.Image => "Image",
                FileViewerKinds.Video => "Video",
                FileViewerKinds.Audio => "Audio",
                _ => "Binary"
            };
        }

        return extension switch
        {
            ".cs" => "C#",
            ".css" => "CSS",
            ".dart" => "Dart",
            ".go" => "Go",
            ".html" => "HTML",
            ".java" => "Java",
            ".js" or ".jsx" => "JavaScript",
            ".json" => "JSON",
            ".kt" or ".kts" => "Kotlin",
            ".py" => "Python",
            ".razor" => "Razor",
            ".rs" => "Rust",
            ".sh" => "Shell",
            ".sql" => "SQL",
            ".swift" => "Swift",
            ".ts" or ".tsx" => "TypeScript",
            ".yaml" or ".yml" => "YAML",
            _ => "Code"
        };
    }

    internal static string ResolveMimeType(string extension, string? suppliedMimeType = null)
    {
        var normalizedMimeType = suppliedMimeType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedMimeType)
            && normalizedMimeType != "application/octet-stream"
            && !(normalizedMimeType == "text/plain" && mediaMimeTypes.ContainsKey(extension)))
        {
            return suppliedMimeType!.Trim();
        }

        if (mediaMimeTypes.TryGetValue(extension, out var mediaMimeType))
        {
            return mediaMimeType;
        }

        return extension switch
        {
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".csv" => "text/csv",
            ".gif" => "image/gif",
            ".glb" => "model/gltf-binary",
            ".gltf" => "model/gltf+json",
            ".html" => "text/html",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".json" or ".geojson" or ".har" => "application/json",
            ".md" or ".markdown" or ".mdx" => "text/markdown",
            ".obj" => "model/obj",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".sqlite" or ".sqlite3" or ".db" or ".db3" => "application/vnd.sqlite3",
            ".stl" => "model/stl",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".xml" or ".xaml" or ".plist" or ".csproj" or ".props" or ".targets" => "application/xml",
            _ when codeExtensions.Contains(extension) => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeExtension(string? extension, string fileName)
    {
        var normalized = string.IsNullOrWhiteSpace(extension) ? Path.GetExtension(fileName) : extension;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }
        return normalized.StartsWith('.') ? normalized.ToLowerInvariant() : $".{normalized.ToLowerInvariant()}";
    }

    private static string CreateColumnKey(string name, int index)
        => string.IsNullOrWhiteSpace(name) ? $"column_{index + 1}" : name;

    private static string FormatDatabaseValue(object value)
        => value switch
        {
            DBNull => "NULL",
            byte[] bytes => $"<BLOB {bytes.Length:N0} bytes>",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private static string FormatJsonValue(JsonNode? node)
    {
        if (node is null)
        {
            return "NULL";
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        if (node is JsonObject blob
            && string.Equals(ReadString(blob, "type"), "blob", StringComparison.OrdinalIgnoreCase))
        {
            return $"<BLOB {ReadInt64(blob, "byteLength"):N0} bytes>";
        }
        return node.ToJsonString();
    }

    private static string? ReadString(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<string>(out var result) ? result : null;

    private static long ReadInt64(JsonObject value, string name)
    {
        if (value[name] is not JsonValue node)
        {
            return 0;
        }
        if (node.TryGetValue<long>(out var result))
        {
            return result;
        }
        return node.TryGetValue<int>(out var integer) ? integer : 0;
    }

    private static bool ReadBoolean(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<bool>(out var result) && result;

    internal static void EnsureSqliteProviderInitialized()
        => _ = sqliteProviderInitialization.Value;

    private static bool InitializeSqliteProvider()
    {
        try
        {
            _ = SQLitePCL.raw.sqlite3_libversion();
        }
        catch
        {
            SQLitePCL.ISQLite3Provider provider = OperatingSystem.IsWindows()
                ? new SQLitePCL.SQLite3Provider_winsqlite3()
                : new SQLitePCL.SQLite3Provider_sqlite3();
            SQLitePCL.raw.SetProvider(provider);
            SQLitePCL.raw.FreezeProvider();
        }
        return true;
    }

    private sealed record DatabaseObjectDefinition(string Name, string Type, string Sql);

    private sealed record LiveFilePreviewDownload(bool IsSuccess, string Message, JsonObject Result)
    {
        public static LiveFilePreviewDownload Success(JsonObject result)
            => new(true, "File preview downloaded.", result);

        public static LiveFilePreviewDownload Failure(string message)
            => new(false, message, new JsonObject());
    }

    private sealed record ResolvedArtifactFile(
        bool IsSuccess,
        string Message,
        string? FilePath,
        SessionArtifactEntry? Entry)
    {
        public static ResolvedArtifactFile Success(string filePath, SessionArtifactEntry entry)
            => new(true, "Artifact file resolved.", filePath, entry);

        public static ResolvedArtifactFile Failure(string message)
            => new(false, message, null, null);
    }
}
