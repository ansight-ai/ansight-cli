using System.Globalization;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceSandboxEntry(string Name, string RelativePath, string Kind,
    long SizeBytes, DateTimeOffset LastModifiedUtc, string Version);
internal sealed record DeviceSandboxChunk(DeviceSandboxEntry File, byte[] Bytes, bool HasMore);

internal sealed partial class DevicePlatformProbe
{
    public async Task<IReadOnlyList<string>> GetFileRootsAsync(CancellationToken token)
    {
        if (IsAndroid) return ["data"];
        var roots = new List<string> { "data", "app" };
        var groups = await commands.RunAsync("/usr/bin/xcrun", ["simctl", "get_app_container", target.DeviceIdentifier,
            target.ApplicationIdentifier, "groups"], token).ConfigureAwait(false);
        // simctl prints one group identifier and path per line; never infer arbitrary host paths as roots.
        if (groups.ExitCode == 0)
            foreach (var line in groups.Text.Split('\n'))
            {
                var name = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim(':');
                if (name?.StartsWith("group.", StringComparison.Ordinal) == true && !name.Any(char.IsControl))
                    roots.Add("group:" + name);
            }
        return roots.Distinct(StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<DeviceSandboxEntry>> BrowseDirectoryAsync(
        string root, string path, bool includeHidden, int maximumEntries, CancellationToken token)
    {
        ValidateRelativePath(path, allowEmpty: true);
        if (!IsAndroid)
        {
            var absolute = ResolveContainedPath(await ContainerAsync(root, token).ConfigureAwait(false), path);
            return new DirectoryInfo(absolute).EnumerateFileSystemInfos()
                .Where(file => includeHidden || !file.Name.StartsWith('.'))
                .Take(maximumEntries).Select(file => LocalEntry(file, JoinRelative(path, file.Name))).ToArray();
        }
        var script = "test -d \"$p\" || exit 1; n=0; for f in \"$p\"/* \"$p\"/.[!.]* \"$p\"/..?*; do "
            + "[ -e \"$f\" ] || [ -L \"$f\" ] || continue; "
            + (includeHidden ? "" : "case \"${f##*/}\" in .*) continue;; esac; ")
            + "n=$((n+1)); [ \"$n\" -le " + maximumEntries.ToString(CultureInfo.InvariantCulture) + " ] || break; "
            + "m=$(stat -c '%F|%s|%Y|%y|%i' \"$f\") || exit 1; printf '%s\\000%s\\000' \"${f##*/}\" \"$m\"; done";
        var result = await SandboxCommandAsync(root, path, script, token, 1_048_576).ConfigureAwait(false);
        result.RequireText();
        var values = Encoding.UTF8.GetString(result.Output).Split('\0');
        var entries = new List<DeviceSandboxEntry>();
        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            if (values[i].Length == 0 || values[i].Any(char.IsControl)) continue;
            entries.Add(ParseAndroidFile(values[i], JoinRelative(path, values[i]), values[i + 1]));
        }
        return entries;
    }

    public async Task<DeviceSandboxEntry> GetFileInfoAsync(string root, string path, CancellationToken token)
    {
        ValidateRelativePath(path);
        if (!IsAndroid)
        {
            var absolute = ResolveContainedPath(await ContainerAsync(root, token).ConfigureAwait(false), path);
            return LocalEntry(new FileInfo(absolute), path);
        }
        var result = await SandboxCommandAsync(root, path, "stat -c '%F|%s|%Y|%y|%i' \"$p\"", token, 4096).ConfigureAwait(false);
        return ParseAndroidFile(Path.GetFileName(path), path, result.RequireText());
    }

    public async Task<DeviceSandboxChunk> ReadFileChunkAsync(string root, string path,
        long offset, int maximumBytes, string? expectedVersion, CancellationToken token)
    {
        if (offset < 0 || offset > 67_108_864 || maximumBytes < 1 || maximumBytes > 1_048_576) throw new ArgumentException("Invalid file range.");
        var before = await GetFileInfoAsync(root, path, token).ConfigureAwait(false);
        if (before.Kind != "file") throw new IOException("Only regular sandbox files can be read.");
        if (before.SizeBytes > 67_108_864) throw new IOException("External file browsing supports files up to 64 MiB.");
        if (expectedVersion is not null && expectedVersion != before.Version)
            throw new IOException("The file changed while it was being read. Refresh the file and try again.");
        var count = (int)Math.Min(maximumBytes, Math.Max(0, before.SizeBytes - offset));
        byte[] bytes;
        if (IsAndroid)
        {
            var read = await SandboxCommandAsync(root, path,
                "test -f \"$p\" && tail -c +" + (offset + 1).ToString(CultureInfo.InvariantCulture)
                + " \"$p\" | head -c " + count.ToString(CultureInfo.InvariantCulture), token, maximumBytes).ConfigureAwait(false);
            read.RequireText();
            bytes = read.Output;
        }
        else
        {
            var absolute = ResolveContainedPath(await ContainerAsync(root, token).ConfigureAwait(false), path);
            await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            stream.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        var after = await GetFileInfoAsync(root, path, token).ConfigureAwait(false);
        if (before.Version != after.Version || bytes.Length != count)
            throw new IOException("The file changed while it was being read. Refresh the file and try again.");
        return new DeviceSandboxChunk(before, bytes, offset + bytes.Length < before.SizeBytes);
    }

    private static DeviceSandboxEntry LocalEntry(FileSystemInfo file, string path)
    {
        var attributes = file.Attributes;
        var kind = attributes.HasFlag(FileAttributes.ReparsePoint) ? "symlink"
            : attributes.HasFlag(FileAttributes.Directory) ? "directory" : "file";
        var length = kind == "file" ? new FileInfo(file.FullName).Length : 0;
        var modified = new DateTimeOffset(file.LastWriteTimeUtc);
        return new DeviceSandboxEntry(file.Name, path, kind, length, modified,
            $"{length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}");
    }

    internal static DeviceSandboxEntry ParseAndroidFile(string name, string path, string metadata)
    {
        var parts = metadata.Trim().Split('|');
        if (parts.Length != 5 || !long.TryParse(parts[1], out var size) || !long.TryParse(parts[2], out var seconds))
            throw new IOException("ADB returned invalid file metadata.");
        var kind = parts[0] == "directory" ? "directory" : parts[0].Contains("regular", StringComparison.Ordinal) ? "file" : "symlink";
        return new DeviceSandboxEntry(name, path, kind, kind == "file" ? size : 0,
            DateTimeOffset.FromUnixTimeSeconds(seconds), $"{size}:{parts[3]}:{parts[4]}");
    }

    private static string JoinRelative(string path, string name) => path.Length == 0 ? name : path.TrimEnd('/') + "/" + name;
}
