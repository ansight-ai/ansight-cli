using System.Text.Json.Nodes;

namespace Ansight.Host.Diagnostics;

public static class SkillInventory
{
    public static JsonObject Inspect(string directory, string scope, string? bundledDirectory = null, CancellationToken cancellationToken = default)
    {
        var id = Path.GetFileName(directory);
        var files = HashFiles(directory, cancellationToken);
        var actual = PackageHash(files);
        var skillPath = Path.Combine(directory, "SKILL.md");
        string? version = null;
        if (File.Exists(skillPath))
        {
            var frontmatter = File.ReadAllText(skillPath).Split("---", 3);
            if (frontmatter.Length == 3)
            {
                var match = System.Text.RegularExpressions.Regex.Match(frontmatter[1], @"(?m)^\s*version:\s*['""']?([\w.+-]+)");
                if (match.Success) version = match.Groups[1].Value;
            }
        }
        var baselineFiles = bundledDirectory is not null && Directory.Exists(bundledDirectory) ? HashFiles(bundledDirectory, cancellationToken) : null;
        var expected = baselineFiles is not null ? PackageHash(baselineFiles) : null;
        var differences = new List<string>();
        if (baselineFiles is not null)
        {
            var byPath = files.ToDictionary(file => file.Path);
            foreach (var item in baselineFiles)
            {
                if (!byPath.Remove(item.Path, out var found)) differences.Add("Missing: " + item.Path);
                else if (found.Sha256 != item.Sha256) differences.Add("Different: " + item.Path);
            }
            differences.AddRange(byPath.Keys.Select(path => "Extra: " + path));
        }
        return new JsonObject
        {
            ["id"] = id, ["version"] = version, ["scope"] = scope, ["path"] = directory,
            ["sha256"] = actual, ["bundledSha256"] = expected,
            ["comparison"] = !File.Exists(skillPath) ? "incomplete" : expected is null ? "no-baseline" : actual == expected ? "matches-bundled" : "differs-from-bundled",
            ["baselinePath"] = bundledDirectory,
            ["files"] = SystemReportService.Node(files), ["differences"] = SystemReportService.Node(differences)
        };
    }

    public static IReadOnlyList<SkillFileDigest> HashFiles(string directory, CancellationToken cancellationToken = default)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked skills require explicit verification.");
        var files = new List<SkillFileDigest>();
        var pending = new Queue<string>(); pending.Enqueue(directory);
        var directoryCount = 1;
        while (pending.TryDequeue(out var current))
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked skill files cannot be verified.");
                if (Directory.Exists(entry))
                {
                    if (++directoryCount > 2000) throw new InvalidDataException("Skill inventory exceeds limits.");
                    pending.Enqueue(entry);
                    continue;
                }
                if (files.Count >= 2000 || new FileInfo(entry).Length > 16 * 1024 * 1024) throw new InvalidDataException("Skill inventory exceeds limits.");
                using var stream = File.OpenRead(entry);
                files.Add(new(Path.GetRelativePath(directory, entry).Replace('\\', '/'), stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
            }
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    public static string PackageHash(IEnumerable<SkillFileDigest> files)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(files.OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => $"{file.Path}\0{file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{file.Sha256}\n")))));

}

public sealed record SkillFileDigest(string Path, long Bytes, string Sha256);
