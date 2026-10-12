using System.IO.Compression;
using System.IO.Hashing;

namespace Ansight.Host.Workspaces.Archives;

/// <summary>Transfers repository-owned ansight/ files without executing or registering them.</summary>
public sealed class WorkspaceArchiveService
{
    private readonly int maximumEntries;
    private readonly long maximumExpandedBytes;

    public WorkspaceArchiveService() : this(100_000, 2L * 1024 * 1024 * 1024) { }

    internal WorkspaceArchiveService(int maximumEntries, long maximumExpandedBytes)
    {
        this.maximumEntries = maximumEntries;
        this.maximumExpandedBytes = maximumExpandedBytes;
    }

    public WorkspaceArchiveResult Export(
        string workspacePath,
        string archivePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        workspacePath = Path.GetFullPath(workspacePath);
        archivePath = NormalizeArchivePath(archivePath);
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRoot = Path.Combine(workspacePath, "ansight");
            RejectLink(workspacePath);
            RejectLink(sourceRoot);
            if (!Directory.Exists(sourceRoot))
                throw new DirectoryNotFoundException($"Workspace '{workspacePath}' does not contain an ansight directory.");
            if (IsWithin(ResolveLinks(archivePath), ResolveLinks(sourceRoot)))
                throw new IOException("Write the exported ZIP outside the workspace's ansight directory.");
            CheckFileDestination(archivePath, overwrite);

            var paths = new ArchivePaths(maximumEntries);
            var entries = new List<SourceEntry>();
            Collect(new DirectoryInfo(sourceRoot), "ansight");

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            temporaryPath = Path.Combine(Path.GetDirectoryName(archivePath)!, $".ansight-export-{Guid.NewGuid():N}.tmp");
            long totalBytes = 0;
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var source in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectPathLinks(workspacePath, source.RelativePath);
                    var entry = archive.CreateEntry(source.RelativePath + (source.IsDirectory ? "/" : ""), CompressionLevel.Optimal);
                    if (source.IsDirectory) continue;
                    if (!OperatingSystem.IsWindows())
                        entry.ExternalAttributes = unchecked((int)((0x8000 | (uint)File.GetUnixFileMode(source.FullPath) & 0x1FF) << 16));
                    using var input = File.OpenRead(source.FullPath);
                    using var output = entry.Open();
                    totalBytes += CopyBounded(input, output, maximumExpandedBytes - totalBytes, cancellationToken);
                }
            }
            CheckFileDestination(archivePath, overwrite);
            File.Move(temporaryPath, archivePath, overwrite);
            return new(true, $"Workspace exported to '{archivePath}'.", workspacePath, archivePath,
                entries.Count(entry => !entry.IsDirectory), totalBytes);

            void Collect(DirectoryInfo directory, string relativePath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(relativePath, isDirectory: true);
                entries.Add(new(directory.FullName, relativePath, true));
                foreach (var item in directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (IsExcluded(item.Name)) continue;
                    RejectLink(item.FullName);
                    var childPath = relativePath + "/" + item.Name;
                    if (item is DirectoryInfo child) Collect(child, childPath);
                    else
                    {
                        paths.Add(childPath, isDirectory: false);
                        entries.Add(new(item.FullName, childPath, false));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, exception.Message, workspacePath, archivePath);
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public WorkspaceArchiveResult Import(
        string archivePath,
        string workspacePath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        workspacePath = Path.GetFullPath(workspacePath);
        archivePath = NormalizeArchivePath(archivePath);
        var stagingPath = Path.Combine(Path.GetTempPath(), $"ansight-workspace-import-{Guid.NewGuid():N}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > maximumEntries)
                throw new InvalidDataException($"A workspace ZIP must contain 1–{maximumEntries} entries under ansight/.");
            var paths = new ArchivePaths(maximumEntries);
            long declaredBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isDirectory = entry.FullName.EndsWith('/');
                var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
                paths.Add(path, isDirectory);
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    || unixType != 0 && unixType != (isDirectory ? 0x4000 : 0x8000))
                    throw new InvalidDataException($"Workspace archives cannot contain links or special files: {entry.FullName}");
                if (isDirectory && entry.Length != 0)
                    throw new InvalidDataException($"Archive directory contains data: {entry.FullName}");
                if (entry.Length > maximumExpandedBytes - declaredBytes)
                    throw new InvalidDataException($"Workspace archive exceeds the {maximumExpandedBytes} byte expanded size limit.");
                declaredBytes += entry.Length;
            }

            ValidateDestination(workspacePath, paths.Nodes, overwrite);
            // Decompress and verify every file before touching the destination workspace.
            Directory.CreateDirectory(stagingPath);
            var payloadPath = Path.Combine(stagingPath, "payload");
            long totalBytes = 0;
            foreach (var node in paths.Nodes.Where(node => node.IsDirectory))
                Directory.CreateDirectory(Path.Combine(payloadPath, node.Path));
            foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
            {
                using var input = entry.Open();
                using var output = new FileStream(Path.Combine(payloadPath, entry.FullName), FileMode.CreateNew, FileAccess.Write);
                var crc = new Crc32();
                var copiedBytes = CopyBounded(input, output, maximumExpandedBytes - totalBytes, cancellationToken, crc);
                if (copiedBytes != entry.Length || crc.GetCurrentHashAsUInt32() != entry.Crc32)
                    throw new InvalidDataException($"Archive entry failed integrity verification: {entry.FullName}");
                if (!OperatingSystem.IsWindows() && ((entry.ExternalAttributes >> 16) & 0xF000) == 0x8000)
                    File.SetUnixFileMode(Path.Combine(payloadPath, entry.FullName), (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF));
                totalBytes += copiedBytes;
            }

            ValidateDestination(workspacePath, paths.Nodes, overwrite);
            cancellationToken.ThrowIfCancellationRequested();
            // Keep originals for rollback if a filesystem error interrupts the merge.
            var committed = new List<ImportedFile>();
            var createdDirectories = new List<string>();
            try
            {
                foreach (var node in paths.Nodes.Where(node => node.IsDirectory))
                    CreateDirectory(Path.Combine(workspacePath, node.Path), createdDirectories);
                foreach (var node in paths.Nodes.Where(node => !node.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectPathLinks(workspacePath, node.Path);
                    var destination = Path.Combine(workspacePath, node.Path);
                    CheckFileDestination(destination, overwrite);
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        backup = Path.Combine(stagingPath, "backup", node.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(destination, backup);
                    }
                    File.Move(Path.Combine(payloadPath, node.Path), destination, overwrite);
                    committed.Add(new(destination, backup));
                }
            }
            catch
            {
                try
                {
                    foreach (var file in committed.AsEnumerable().Reverse())
                    {
                        if (file.BackupPath is null) File.Delete(file.Path);
                        else File.Move(file.BackupPath, file.Path, overwrite: true);
                    }
                    foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                        Directory.Delete(directory);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    var retainedPath = stagingPath;
                    stagingPath = string.Empty;
                    throw new IOException($"Import rollback failed. Recovery files are retained at '{retainedPath}'. {rollbackException.Message}", rollbackException);
                }
                throw;
            }
            return new(true, $"Workspace imported into '{workspacePath}'.", workspacePath, archivePath,
                paths.Nodes.Count(node => !node.IsDirectory), totalBytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, exception.Message, workspacePath, archivePath);
        }
        finally
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        }
    }

    private static void ValidateDestination(string workspacePath, IEnumerable<ArchiveNode> nodes, bool overwrite)
    {
        RejectLink(workspacePath);
        if (File.Exists(workspacePath)) throw new IOException($"Workspace path is a file: {workspacePath}");
        foreach (var node in nodes)
        {
            RejectPathLinks(workspacePath, node.Path);
            var destination = Path.Combine(workspacePath, node.Path);
            if (node.IsDirectory)
            {
                if (File.Exists(destination)) throw new IOException($"A file blocks the archive directory: {destination}");
            }
            else CheckFileDestination(destination, overwrite);
        }
    }

    private static void CheckFileDestination(string path, bool overwrite)
    {
        RejectLink(path);
        if (Directory.Exists(path)) throw new IOException($"A directory blocks the destination file: {path}");
        if (!overwrite && File.Exists(path)) throw new IOException($"File already exists: {path}. Use --force to replace it.");
    }

    private static void RejectPathLinks(string root, string relativePath)
    {
        RejectLink(root);
        foreach (var segment in relativePath.Split('/'))
        {
            root = Path.Combine(root, segment);
            RejectLink(root);
        }
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists || Directory.Exists(path)) && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Workspace archives cannot follow filesystem links: {path}");
    }

    private static void CreateDirectory(string path, List<string> created)
    {
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) CreateDirectory(parent, created);
        Directory.CreateDirectory(path);
        created.Add(path);
    }

    private static long CopyBounded(Stream input, Stream output, long limit, CancellationToken cancellationToken, Crc32? crc = null)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > limit - copied) throw new InvalidDataException("Workspace archive exceeds the expanded size limit.");
            output.Write(buffer, 0, count);
            crc?.Append(buffer.AsSpan(0, count));
            copied += count;
        }
        return copied;
    }

    private static string NormalizeArchivePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The workspace archive path must end in .zip.");
        return fullPath;
    }

    private static bool IsWithin(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ResolveLinks(string path, int depth = 0)
    {
        if (depth > 64) throw new IOException("Too many filesystem links in the archive path.");
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            var info = new DirectoryInfo(resolved);
            if (info.LinkTarget is not null)
                resolved = ResolveLinks(info.ResolveLinkTarget(returnFinalTarget: true)!.FullName, depth + 1);
        }
        return resolved;
    }

    private static bool IsExcluded(string name)
        => name.Equals(".git", StringComparison.OrdinalIgnoreCase)
           || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
           || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceEntry(string FullPath, string RelativePath, bool IsDirectory);
    private sealed record ImportedFile(string Path, string? BackupPath);
    private sealed record ArchiveNode(string Path, bool IsDirectory);

    private sealed class ArchivePaths(int maximumEntries)
    {
        private readonly Dictionary<string, ArchiveNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> explicitPaths = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<ArchiveNode> Nodes => nodes.Values;

        public void Add(string path, bool isDirectory)
        {
            var segments = path.Split('/');
            if (segments[0] != "ansight" || segments.Length == 1 && !isDirectory
                || segments.Length > 64 || path.Length > 4096
                || segments.Any(IsUnsafeSegment))
                throw new InvalidDataException($"Unsafe workspace archive path: '{path}'. Entries must be under ansight/.");
            if (!explicitPaths.Add(path.Normalize(NormalizationForm.FormC)))
                throw new InvalidDataException($"Duplicate workspace archive path: {path}");
            if (explicitPaths.Count > maximumEntries)
                throw new InvalidDataException($"Workspace archive exceeds the {maximumEntries} entry limit.");
            for (var index = 0; index < segments.Length; index++)
            {
                var prefix = string.Join('/', segments.Take(index + 1));
                var directory = index < segments.Length - 1 || isDirectory;
                var key = prefix.Normalize(NormalizationForm.FormC);
                if (nodes.TryGetValue(key, out var previous))
                {
                    if (previous.Path != prefix || previous.IsDirectory != directory)
                        throw new InvalidDataException($"Conflicting workspace archive paths: {previous.Path}, {path}");
                }
                else
                {
                    if (nodes.Count >= maximumEntries)
                        throw new InvalidDataException($"Workspace archive exceeds the {maximumEntries} entry limit, including parent directories.");
                    nodes.Add(key, new(prefix, directory));
                }
            }
        }

        private static bool IsUnsafeSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith('.')
                || segment.EndsWith(' ') || segment.Any(character => char.IsControl(character) || "\\:<>\"|?*".Contains(character))
                || IsExcluded(segment)) return true;
            var stem = segment.Split('.')[0];
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                   || stem.Length == 4 && stem[3] is >= '1' and <= '9'
                   && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
        }
    }
}
