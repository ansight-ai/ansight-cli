using System.IO.Compression;

namespace Ansight.Host.Files;

internal static class ArtifactArchiveComparer
{
    internal static async Task<ArtifactFileDiff> CompareAsync(string beforePath, string afterPath, string path, ArtifactDiffRequest request, CancellationToken cancellationToken)
    {
        var changes = new List<ArtifactChange>();
        var complete = true;
        using var before = ZipFile.OpenRead(beforePath);
        using var after = ZipFile.OpenRead(afterPath);
        if (before.Entries.Count + after.Entries.Count > 4000 || before.Entries.Sum(entry => entry.Length) + after.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024)
            return new(path, "zip", "changed", false, false, "Archive comparison exceeds 4,000 members or 64 MiB expanded size.", 0, [], null);
        var left = before.Entries.Where(entry => entry.Name.Length > 0).ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        var right = after.Entries.Where(entry => entry.Name.Length > 0).ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(), "ansight-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var member in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                left.TryGetValue(member, out var oldEntry); right.TryGetValue(member, out var newEntry);
                if (oldEntry is null || newEntry is null)
                {
                    ArtifactFileComparer.Add(changes, new(member, oldEntry is null ? "added" : "removed", oldEntry?.FullName, newEntry?.FullName));
                    continue;
                }
                // Never extract using archive-controlled paths or recurse into nested archives.
                var oldFile = Path.Combine(directory, "before"); var newFile = Path.Combine(directory, "after");
                await CopyAsync(oldEntry, oldFile, cancellationToken).ConfigureAwait(false);
                await CopyAsync(newEntry, newFile, cancellationToken).ConfigureAwait(false);
                var extension = Path.GetExtension(member).TrimStart('.').ToLowerInvariant();
                if (extension is "db" or "db3" or "sqlite3") extension = "sqlite";
                if (extension == "zip")
                {
                    var oldHash = await ArtifactFileComparer.HashAsync(oldFile, cancellationToken).ConfigureAwait(false);
                    var newHash = await ArtifactFileComparer.HashAsync(newFile, cancellationToken).ConfigureAwait(false);
                    if (oldHash != newHash) ArtifactFileComparer.Add(changes, new(member, "binary", oldHash, newHash));
                    continue;
                }
                var result = await ArtifactFileComparer.CompareAsync(oldFile, newFile, member, extension,
                    request with { Offset = 0, Limit = 1000 }, cancellationToken).ConfigureAwait(false);
                complete &= result.IsComplete && result.NextOffset is null;
                foreach (var change in result.Changes) ArtifactFileComparer.Add(changes, change with { Path = member + ":" + change.Path });
                if (result.Status == "changed" && result.Changes.Count == 0)
                    ArtifactFileComparer.Add(changes, new(member, "binary", "before", result.Message ?? "Content changed"));
            }
        }
        catch (ArtifactFileComparer.ChangeLimitException) { complete = false; }
        finally { Directory.Delete(directory, true); }
        return ArtifactFileComparer.Page(path, "zip", changes, request, complete,
            complete ? "Nested archives compare by hash." : "One or more archive members exceeded comparison limits.");
    }

    private static async Task CopyAsync(ZipArchiveEntry entry, string path, CancellationToken cancellationToken)
    {
        await using var source = entry.Open(); await using var target = File.Create(path);
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > 32 * 1024 * 1024) throw new InvalidDataException("Archive member exceeds 32 MiB expanded size.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}
