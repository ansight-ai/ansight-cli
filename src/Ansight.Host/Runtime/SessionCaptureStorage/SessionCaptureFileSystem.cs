namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Pairing.Models;
using System.Globalization;
using System.Security.Cryptography;

internal static class SessionCaptureFileSystem
{
    internal static void WriteJsonAtomic<T>(string filePath, T value)
        => WriteJsonAtomic(filePath, value, JsonUtil.Pretty);

    internal static void WriteJsonAtomic<T>(string filePath, T value, JsonSerializerOptions options)
    {
        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       tempFilePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, value, options);
            }

            File.Move(tempFilePath, filePath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(tempFilePath);
        }
    }

    internal static void WriteAllBytesAtomic(string filePath, byte[] contents)
    {
        WriteAllBytesAtomic(filePath, contents.AsSpan());
    }

    internal static void WriteAllBytesAtomic(string filePath, ReadOnlySpan<byte> contents)
    {
        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempFilePath, contents);
        File.Move(tempFilePath, filePath, overwrite: true);
    }

    internal static void WriteVisualTreeSnapshotDocuments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionVisualTreeSnapshot> snapshots)
    {
        var persistedState = LoadPersistedVisualTreeSnapshotDocumentState(layout.VisualTreesDirectoryPath);
        ReconcileVisualTreeSnapshotDocuments(layout, savedAtUtc, snapshots, persistedState);
    }

    internal static void ReconcileVisualTreeSnapshotDocuments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionVisualTreeSnapshot> snapshots,
        PersistedSnapshotDocumentCollectionState persistedState)
    {
        ReconcileSnapshotDocuments(
            layout.VisualTreesDirectoryPath,
            snapshots,
            persistedState,
            snapshot => snapshot.SnapshotId,
            snapshot => snapshot.CapturedAtUtc,
            snapshot => GetVisualTreeSnapshotFilePath(layout.VisualTreesDirectoryPath, snapshot.CapturedAtUtc),
            snapshot => new SessionVisualTreeSnapshotDocument
            {
                SavedAtUtc = savedAtUtc,
                Header = SessionVisualTreeSnapshotHeader.Create(snapshot),
                Snapshot = snapshot
            });

    }

    internal static string GetVisualTreeSnapshotFilePath(string visualTreesDirectoryPath, DateTimeOffset capturedAtUtc)
        => Path.Combine(visualTreesDirectoryPath, BuildVisualTreeSnapshotFileName(capturedAtUtc));

    internal static string BuildVisualTreeSnapshotFileName(DateTimeOffset capturedAtUtc)
        => $"{capturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss.fffffff'Z'", CultureInfo.InvariantCulture)}.json";

    internal static void WriteArtifactSnapshotDocuments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionArtifactSnapshot> snapshots)
    {
        var persistedState = LoadPersistedArtifactSnapshotDocumentState(layout.ArtifactsDirectoryPath);
        ReconcileArtifactSnapshotDocuments(layout, savedAtUtc, snapshots, persistedState);
    }

    internal static void ReconcileArtifactSnapshotDocuments(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionArtifactSnapshot> snapshots,
        PersistedSnapshotDocumentCollectionState persistedState)
    {
        ReconcileSnapshotDocuments(
            layout.ArtifactsDirectoryPath,
            snapshots,
            persistedState,
            snapshot => snapshot.SnapshotId,
            snapshot => snapshot.CapturedAtUtc,
            snapshot => GetArtifactSnapshotFilePath(layout.ArtifactsDirectoryPath, snapshot),
            snapshot => new SessionArtifactSnapshotDocument
            {
                SavedAtUtc = savedAtUtc,
                Header = CreateArtifactSnapshotHeader(snapshot),
                Snapshot = snapshot
            });
    }

    internal static SessionArtifactSnapshotHeader CreateArtifactSnapshotHeader(SessionArtifactSnapshot snapshot)
    {
        return new SessionArtifactSnapshotHeader
        {
            SnapshotId = snapshot.SnapshotId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            Source = snapshot.Source,
            RootAlias = snapshot.RootAlias,
            RelativePath = snapshot.RelativePath,
            Name = snapshot.Name,
            Kind = snapshot.Kind,
            ArtifactDirectoryName = snapshot.ArtifactDirectoryName,
            DirectoryCount = snapshot.DirectoryCount,
            FileCount = snapshot.FileCount,
            ByteCount = snapshot.ByteCount,
            Truncated = snapshot.Truncated
        };
    }

    internal static string GetArtifactSnapshotFilePath(string artifactsDirectoryPath, SessionArtifactSnapshot snapshot)
        => Path.Combine(artifactsDirectoryPath, BuildArtifactSnapshotFileName(snapshot));

    internal static string BuildArtifactSnapshotFileName(SessionArtifactSnapshot snapshot)
    {
        var timestamp = snapshot.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return $"{timestamp}-{FileNameUtil.Sanitize(snapshot.SnapshotId)}.json";
    }

    internal static PersistedSnapshotDocumentCollectionState LoadPersistedVisualTreeSnapshotDocumentState(string directoryPath)
    {
        var state = new PersistedSnapshotDocumentCollectionState();
        if (!Directory.Exists(directoryPath))
        {
            return state;
        }

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(filePath => filePath, StringComparer.Ordinal))
        {
            state.FilePaths.Add(filePath);
            var document = SessionSnapshotReader.TryLoadDocument<SessionVisualTreeSnapshotDocument>(filePath);
            if (document?.Snapshot is not { } snapshot
                || !string.Equals(document.Schema, SessionVisualTreeSnapshotDocument.SchemaName, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            {
                continue;
            }

            AddPersistedSnapshotDocumentState(state, filePath, snapshot.SnapshotId, snapshot.CapturedAtUtc, snapshot);
        }

        return state;
    }

    internal static PersistedSnapshotDocumentCollectionState LoadPersistedArtifactSnapshotDocumentState(string directoryPath)
    {
        var state = new PersistedSnapshotDocumentCollectionState();
        if (!Directory.Exists(directoryPath))
        {
            return state;
        }

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(filePath => filePath, StringComparer.Ordinal))
        {
            state.FilePaths.Add(filePath);
            var snapshot = SessionSnapshotReader.TryLoadDocument<SessionArtifactSnapshotDocument>(filePath)?.Snapshot
                           ?? SessionSnapshotReader.TryLoadDocument<SessionArtifactSnapshot>(filePath);
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            {
                continue;
            }

            AddPersistedSnapshotDocumentState(state, filePath, snapshot.SnapshotId, snapshot.CapturedAtUtc, snapshot);
        }

        return state;
    }

    internal static void AddPersistedSnapshotDocumentState<TSnapshot>(
        PersistedSnapshotDocumentCollectionState state,
        string filePath,
        string snapshotId,
        DateTimeOffset capturedAtUtc,
        TSnapshot snapshot)
    {
        if (state.DocumentsBySnapshotId.TryGetValue(snapshotId, out var existing)
            && existing.CapturedAtUtc >= capturedAtUtc)
        {
            return;
        }

        state.DocumentsBySnapshotId[snapshotId] = new PersistedSnapshotDocumentState
        {
            FilePath = filePath,
            CapturedAtUtc = capturedAtUtc,
            ContentFingerprint = ComputeSnapshotContentFingerprint(snapshot)
        };
    }

    internal static void ReconcileSnapshotDocuments<TSnapshot, TDocument>(
        string directoryPath,
        IReadOnlyList<TSnapshot> snapshots,
        PersistedSnapshotDocumentCollectionState persistedState,
        Func<TSnapshot, string> snapshotIdSelector,
        Func<TSnapshot, DateTimeOffset> capturedAtUtcSelector,
        Func<TSnapshot, string> preferredFilePathSelector,
        Func<TSnapshot, TDocument> documentFactory)
        where TSnapshot : class
    {
        Directory.CreateDirectory(directoryPath);

        var ownerByFilePath = persistedState.DocumentsBySnapshotId
            .ToDictionary(entry => entry.Value.FilePath, entry => entry.Key, StringComparer.Ordinal);
        var updatedDocumentsBySnapshotId = new Dictionary<string, PersistedSnapshotDocumentState>(StringComparer.Ordinal);
        var expectedFilePaths = new HashSet<string>(StringComparer.Ordinal);
        var seenSnapshotIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var snapshot in snapshots
                     .OrderBy(capturedAtUtcSelector)
                     .ThenBy(snapshotIdSelector, StringComparer.Ordinal))
        {
            var snapshotId = snapshotIdSelector(snapshot);
            if (!seenSnapshotIds.Add(snapshotId))
            {
                continue;
            }

            persistedState.DocumentsBySnapshotId.TryGetValue(snapshotId, out var existing);
            var fingerprint = ReferenceEquals(existing?.PersistedSource, snapshot)
                ? existing.ContentFingerprint
                : ComputeSnapshotContentFingerprint(snapshot);
            if (existing is not null
                && string.Equals(existing.ContentFingerprint, fingerprint, StringComparison.Ordinal))
            {
                existing.PersistedSource = snapshot;
                updatedDocumentsBySnapshotId[snapshotId] = existing;
                expectedFilePaths.Add(existing.FilePath);
                continue;
            }

            var capturedAtUtc = capturedAtUtcSelector(snapshot);
            var preferredFilePath = existing is not null && existing.CapturedAtUtc == capturedAtUtc
                ? existing.FilePath
                : preferredFilePathSelector(snapshot);
            var filePath = ResolveAvailableSnapshotDocumentFilePath(
                preferredFilePath,
                snapshotId,
                ownerByFilePath,
                expectedFilePaths);
            WriteJsonAtomic(filePath, documentFactory(snapshot), JsonUtil.Compact);

            var updated = new PersistedSnapshotDocumentState
            {
                FilePath = filePath,
                CapturedAtUtc = capturedAtUtc,
                ContentFingerprint = fingerprint,
                PersistedSource = snapshot
            };
            updatedDocumentsBySnapshotId[snapshotId] = updated;
            expectedFilePaths.Add(filePath);
            ownerByFilePath[filePath] = snapshotId;
        }

        foreach (var filePath in persistedState.FilePaths)
        {
            if (!expectedFilePaths.Contains(filePath))
            {
                DeleteIfExists(filePath);
            }
        }

        persistedState.DocumentsBySnapshotId.Clear();
        foreach (var entry in updatedDocumentsBySnapshotId)
        {
            persistedState.DocumentsBySnapshotId.Add(entry.Key, entry.Value);
        }

        persistedState.FilePaths.Clear();
        persistedState.FilePaths.UnionWith(expectedFilePaths);
    }

    internal static string ResolveAvailableSnapshotDocumentFilePath(
        string preferredFilePath,
        string snapshotId,
        IReadOnlyDictionary<string, string> ownerByFilePath,
        IReadOnlySet<string> expectedFilePaths)
    {
        if (!expectedFilePaths.Contains(preferredFilePath)
            && (!ownerByFilePath.TryGetValue(preferredFilePath, out var ownerSnapshotId)
                || string.Equals(ownerSnapshotId, snapshotId, StringComparison.Ordinal)))
        {
            return preferredFilePath;
        }

        var directoryPath = Path.GetDirectoryName(preferredFilePath)!;
        var baseName = Path.GetFileNameWithoutExtension(preferredFilePath);
        var sanitizedSnapshotId = FileNameUtil.Sanitize(snapshotId);
        for (var suffix = 1; ; suffix++)
        {
            var suffixText = suffix == 1 ? string.Empty : $"-{suffix}";
            var candidatePath = Path.Combine(directoryPath, $"{baseName}-{sanitizedSnapshotId}{suffixText}.json");
            if (!expectedFilePaths.Contains(candidatePath)
                && (!ownerByFilePath.TryGetValue(candidatePath, out ownerSnapshotId)
                    || string.Equals(ownerSnapshotId, snapshotId, StringComparison.Ordinal)))
            {
                return candidatePath;
            }
        }
    }

    internal static string ComputeSnapshotContentFingerprint<TSnapshot>(TSnapshot snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonUtil.Compact);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static void WriteImportedArtifactFiles(
        SessionPathLayout layout,
        IReadOnlyDictionary<string, byte[]>? artifactBytesByRelativePath)
    {
        if (artifactBytesByRelativePath is null || artifactBytesByRelativePath.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(layout.ArtifactsDirectoryPath);
        foreach (var (relativePath, bytes) in artifactBytesByRelativePath)
        {
            var normalizedRelativePath = NormalizeArchiveRelativePath(relativePath);
            if (string.IsNullOrWhiteSpace(normalizedRelativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(layout.ArtifactsDirectoryPath, normalizedRelativePath));
            var artifactsRootPath = Path.GetFullPath(layout.ArtifactsDirectoryPath);
            if (!destinationPath.StartsWith(artifactsRootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(destinationPath, artifactsRootPath, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            WriteAllBytesAtomic(destinationPath, bytes);
        }
    }

    internal static void CopyDirectoryContents(string sourceDirectoryPath, string destinationDirectoryPath)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectoryPath, directoryPath);
            Directory.CreateDirectory(Path.Combine(destinationDirectoryPath, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectoryPath, filePath);
            var destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    internal static string NormalizeArchiveRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var segments = normalized
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != "." && segment != "..")
            .ToArray();
        return segments.Length == 0 ? string.Empty : Path.Combine(segments);
    }

    internal static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    internal static void TryDeleteDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }

    internal static long GetDirectorySize(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        long size = 0;
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(filePath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return size;
        }

        return size;
    }

    internal static long GetFileSize(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return 0;
        }

        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string? NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, JsonUtil.Compact);
        }
        catch
        {
            return json.Trim();
        }
    }

    internal static bool TryDecodeApplicationIcon(DeviceApplicationIconProfile? icon, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(icon?.DataBase64))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(icon.DataBase64.Trim());
            return bytes.Length > 0;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    internal static string ResolveApplicationIconFormat(DeviceApplicationIconProfile? icon)
    {
        var format = icon?.Format;
        if (!string.IsNullOrWhiteSpace(format))
        {
            if (format.Contains('/', StringComparison.Ordinal))
            {
                return ResolveApplicationIconFormatFromMimeType(format);
            }

            return SessionAppIconArtifactPath.ResolveFileExtension(format);
        }

        return ResolveApplicationIconFormatFromMimeType(icon?.MimeType);
    }

    internal static string ResolveApplicationIconFormatFromMimeType(string? mimeType)
    {
        return mimeType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => "jpg",
            "image/webp" => "webp",
            _ => "png"
        };
    }

    internal static string ResolveApplicationIconMimeType(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
    }

}
