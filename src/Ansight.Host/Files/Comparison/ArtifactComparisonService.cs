namespace Ansight.Host.Files;

/// <summary>Compares retained evidence only; never requests a new capture from an app.</summary>
public sealed class ArtifactComparisonService(RuntimeCoordinator runtime)
{
    public async Task<ArtifactListResult> ListAsync(ArtifactListRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("sessionId is required.");
        if (request.Offset < 0 || request.Limit is < 1 or > 1000) throw new ArgumentException("Offset must be nonnegative and limit must be 1–1000.");
        if (request.From > request.To || request.MinBytes < 0 || request.MaxBytes < 0 || request.MinBytes > request.MaxBytes)
            throw new ArgumentException("Invalid time or size range.");
        var session = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, null, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"Session '{request.SessionId}' was not found.");
        IEnumerable<ArtifactListItem> items = session.ArtifactSnapshots.Select(snapshot => Describe(session, snapshot));
        items = items.Where(item =>
            (string.IsNullOrWhiteSpace(request.Search) || string.Join(' ', new[] { item.Reference, item.Name, item.Provider, item.LogicalId }.Concat(item.Paths)).Contains(request.Search, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Provider) || item.Provider.Equals(request.Provider, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Format) || item.Formats.Contains(request.Format.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            && (!request.From.HasValue || item.CapturedAtUtc >= request.From)
            && (!request.To.HasValue || item.CapturedAtUtc <= request.To)
            && (!request.MinBytes.HasValue || item.SizeBytes >= request.MinBytes)
            && (!request.MaxBytes.HasValue || item.SizeBytes <= request.MaxBytes));
        items = request.Sort switch
        {
            "captured-at" => items.OrderBy(item => item.CapturedAtUtc).ThenBy(item => item.ArtifactId, StringComparer.Ordinal),
            "newest" => items.OrderByDescending(item => item.CapturedAtUtc).ThenBy(item => item.ArtifactId, StringComparer.Ordinal),
            "name" => items.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.ArtifactId, StringComparer.Ordinal),
            "size" => items.OrderBy(item => item.SizeBytes).ThenBy(item => item.ArtifactId, StringComparer.Ordinal),
            _ => throw new ArgumentException("Sort must be captured-at, newest, name, or size.")
        };
        var matches = items.ToArray();
        var page = matches.Skip(request.Offset).Take(request.Limit).ToArray();
        return new("ansight.artifact-list/v1", page, matches.Length,
            request.Offset + page.Length < matches.Length ? request.Offset + page.Length : null);
    }

    public async Task<ArtifactDiffResult> CompareAsync(ArtifactDiffRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Artifacts is null || request.Artifacts.Count is < 2 or > 32)
            throw new ArgumentException("Select between 2 and 32 qualified artifact references.");
        if (request.Mode is not ("auto" or "text")) throw new ArgumentException("Mode must be auto or text.");
        if (request.Offset < 0 || request.Limit is < 1 or > 1000) throw new ArgumentException("Offset must be nonnegative and limit must be 1–1000.");
        var resolved = new List<ResolvedCapture>();
        // Resolve every reference before producing any comparisons. Never guess a session.
        var sessions = new Dictionary<string, AppSessionSnapshot>(StringComparer.Ordinal);
        foreach (var reference in request.Artifacts)
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.SessionId) || string.IsNullOrWhiteSpace(reference.ArtifactId))
                throw new ArgumentException("Each artifact requires sessionId and artifactId.");
            if (!sessions.TryGetValue(reference.SessionId, out var session))
            {
                session = await runtime.Sessions.LoadSnapshotAsync(reference.SessionId, null, cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException($"Session '{reference.SessionId}' was not found.");
                sessions.Add(reference.SessionId, session);
            }
            var matches = session.ArtifactSnapshots.Where(item => item.SnapshotId == reference.ArtifactId).ToArray();
            if (matches.Length != 1) throw new ArgumentException($"Artifact '{reference.Reference}' was not found or is ambiguous.");
            resolved.Add(new(session, matches[0], Describe(session, matches[0])));
        }
        var hops = new List<ArtifactDiffHop>();
        for (var index = 1; index < resolved.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = resolved[index - 1];
            var after = resolved[index];
            var files = await CompareCapturesAsync(before, after, request, cancellationToken).ConfigureAwait(false);
            var complete = !before.Snapshot.Truncated && !after.Snapshot.Truncated && files.All(file => file.IsComplete);
            var status = !complete ? "incomplete" : files.Any(file => file.Status is not ("identical" or "equivalent")) ? "changed"
                : files.All(file => file.ByteIdentical) ? "identical" : "equivalent";
            hops.Add(new(index, before.Description, after.Description, status, complete, files));
        }
        var completeJourney = hops.All(hop => hop.IsComplete);
        var summary = $"{hops.Count(hop => hop.Status == "changed")} changed hops; {hops.Count(hop => hop.Status is "identical" or "equivalent")} unchanged hops; {hops.Count(hop => !hop.IsComplete)} incomplete hops.";
        if (resolved.Count > 2 && completeJourney && hops.Any(hop => hop.Status == "changed"))
        {
            var endpoints = await CompareCapturesAsync(resolved[0], resolved[^1], request, cancellationToken).ConfigureAwait(false);
            if (endpoints.All(file => file.IsComplete && file.Status is "identical" or "equivalent"))
                summary += " The journey returned to its initial content after intermediate changes.";
        }
        return new("ansight.artifact-diff/v1", resolved.Select(item => item.Description).ToArray(), hops, completeJourney, summary);
    }

    private async Task<IReadOnlyList<ArtifactFileDiff>> CompareCapturesAsync(ResolvedCapture before, ResolvedCapture after, ArtifactDiffRequest request, CancellationToken cancellationToken)
    {
        var left = Entries(before.Snapshot, request.Path);
        var right = Entries(after.Snapshot, request.Path);
        if (left.Count == 0 && right.Count == 0)
            return [new(request.Path ?? "", "unknown", "unavailable", false, false, "No matching captured files.", 0, [], null)];
        var files = new List<ArtifactFileDiff>();
        // Generated filenames commonly contain timestamps. Single-file captures are explicitly paired by the user.
        if (left.Count == 1 && right.Count == 1 && before.Snapshot.FileCount == 1 && after.Snapshot.FileCount == 1)
        {
            files.Add(await CompareEntriesAsync(before, left.Values.Single(), after, right.Values.Single(), request, cancellationToken).ConfigureAwait(false));
            return files;
        }
        foreach (var path in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(path, out var oldEntry);
            right.TryGetValue(path, out var newEntry);
            if (oldEntry is null || newEntry is null)
            {
                var unknown = oldEntry is null ? before.Snapshot.Truncated : after.Snapshot.Truncated;
                var owner = oldEntry is null ? after : before;
                try
                {
                    var content = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(owner.Session.SessionId, owner.Snapshot.SnapshotId, path, cancellationToken).ConfigureAwait(false);
                    if (!content.IsSuccess)
                    {
                        files.Add(new(path, Format(newEntry ?? oldEntry!), "unavailable", false, false, content.Message, 0, [], null));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    files.Add(new(path, Format(newEntry ?? oldEntry!), "unavailable", false, false, exception.Message, 0, [], null));
                    continue;
                }
                files.Add(new(path, Format(newEntry ?? oldEntry!), unknown ? "unknown" : oldEntry is null ? "added" : "removed", false, !unknown,
                    unknown ? "The other capture is incomplete; absence does not establish addition or deletion." : null,
                    1, request.Offset == 0 ? [new(path, unknown ? "unknown" : oldEntry is null ? "added" : "removed", oldEntry?.Name, newEntry?.Name)] : [], null));
            }
            else files.Add(await CompareEntriesAsync(before, oldEntry, after, newEntry, request, cancellationToken).ConfigureAwait(false));
        }
        return files;
    }

    private async Task<ArtifactFileDiff> CompareEntriesAsync(ResolvedCapture before, SessionArtifactEntry left, ResolvedCapture after, SessionArtifactEntry right, ArtifactDiffRequest request, CancellationToken cancellationToken)
    {
        var path = right.SnapshotRelativePath;
        try
        {
            var oldFile = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(before.Session.SessionId, before.Snapshot.SnapshotId, left.SnapshotRelativePath, cancellationToken).ConfigureAwait(false);
            var newFile = await runtime.FileVisualizations.ResolveSessionArtifactContentAsync(after.Session.SessionId, after.Snapshot.SnapshotId, right.SnapshotRelativePath, cancellationToken).ConfigureAwait(false);
            if (!oldFile.IsSuccess || !newFile.IsSuccess || oldFile.FilePath is null || newFile.FilePath is null)
                return new(path, Format(right), "unavailable", false, false, !oldFile.IsSuccess ? oldFile.Message : newFile.Message, 0, [], null);
            return await ArtifactFileComparer.CompareAsync(oldFile.FilePath, newFile.FilePath, path, Format(right), request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or ArgumentException)
        {
            return new(path, Format(right), "error", false, false, exception.Message, 0, [], null);
        }
    }

    private static Dictionary<string, SessionArtifactEntry> Entries(SessionArtifactSnapshot snapshot, string? path)
        => snapshot.Entries.Where(entry => !entry.Kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(path) || entry.SnapshotRelativePath.Equals(path, StringComparison.Ordinal)))
            .ToDictionary(entry => entry.SnapshotRelativePath, StringComparer.Ordinal);

    public static ArtifactListItem Describe(AppSessionSnapshot session, SessionArtifactSnapshot snapshot)
        => new(session.SessionId, session.Name ?? session.ClientName, snapshot.SnapshotId,
            new ArtifactReference(session.SessionId, snapshot.SnapshotId).Reference, snapshot.Name,
            snapshot.RootAlias, snapshot.RelativePath, snapshot.CapturedAtUtc, snapshot.FileCount, snapshot.ByteCount,
            snapshot.Truncated, snapshot.Entries.Where(entry => entry.Kind != "directory").Select(Format).Distinct().Order().ToArray(),
            snapshot.Entries.Select(entry => entry.SnapshotRelativePath).ToArray());

    internal static string Format(SessionArtifactEntry entry)
    {
        var extension = (string.IsNullOrEmpty(entry.FileExtension) ? Path.GetExtension(entry.Name) : entry.FileExtension).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "db" or "db3" or "sqlite3" => "sqlite",
            "markdown" => "md",
            "json" or "xml" or "md" or "sqlite" or "csv" or "tsv" or "zip" => extension,
            _ => entry.MimeType.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json"
                : entry.MimeType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml"
                : entry.MimeType.Contains("sqlite", StringComparison.OrdinalIgnoreCase) ? "sqlite"
                : extension.Length > 0 ? extension : "binary"
        };
    }

    private sealed record ResolvedCapture(AppSessionSnapshot Session, SessionArtifactSnapshot Snapshot, ArtifactListItem Description);
}
