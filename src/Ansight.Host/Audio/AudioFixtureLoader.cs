using System.Buffers.Binary;

namespace Ansight.Host.Audio;

internal static class AudioFixtureLoader
{
    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumDurationMs = 15_000;

    public static async Task<AudioFixture> LoadAsync(string file, string destination, CancellationToken cancellationToken)
    {
        var path = ResolvePath(file, AudioFixtureScope.CurrentRepositoryRoot);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 44 or > MaximumFileBytes)
            throw Invalid("Audio must be a nonempty WAV file no larger than 1 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw Invalid("The audio file changed while it was read.");
        var fixture = Parse(bytes, destination);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
        return fixture;
    }

    internal static string ResolvePath(string file, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0')) throw Invalid("Provide an audio file path.");
        if (Uri.TryCreate(file, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw Invalid("Audio must be a local file, not a URL.");
        if (repositoryRoot is null) return Path.GetFullPath(file);
        if (Path.IsPathRooted(file)) throw Invalid("Task audio paths must be relative to the task repository root.");
        var root = Path.TrimEndingDirectorySeparator(ResolveLinks(Path.GetFullPath(repositoryRoot)));
        var candidate = ResolveLinks(Path.GetFullPath(file, repositoryRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
            throw Invalid("Task audio files must remain inside the task repository, including symbolic link targets.");
        return candidate;
    }

    private static string ResolveLinks(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                          ?? throw Invalid("Could not resolve the audio file's symbolic link.");
        }
        return Path.GetFullPath(current);
    }

    internal static AudioFixture Parse(byte[] bytes, string snapshotPath)
    {
        if (bytes.Length is < 44 or > MaximumFileBytes) throw Invalid("Invalid WAV size.");
        ReadOnlySpan<byte> data = bytes;
        if (!data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) != bytes.Length - 8)
            throw Invalid("Expected a complete RIFF/WAVE file with a matching declared size.");
        var seenFormat = false;
        var seenData = false;
        byte[]? pcm = null;
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw Invalid("Truncated WAV chunk header.");
            var kind = data.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var end = (long)offset + 8 + length;
            var paddedEnd = end + (length & 1);
            if (paddedEnd > bytes.Length) throw Invalid("Truncated WAV chunk.");
            var chunk = data.Slice(offset + 8, checked((int)length));
            if (kind.SequenceEqual("fmt "u8))
            {
                if (seenFormat || (length != 16 && (length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(chunk[16..]) != length - 18))) throw Invalid("Invalid or duplicate WAV format chunk.");
                seenFormat = true;
                if (BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 16_000
                    || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 32_000
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16)
                    throw Invalid("Audio injection supports PCM16 WAV, mono, 16 kHz. Convert the fixture before running the test.");
            }
            else if (kind.SequenceEqual("data"u8))
            {
                if (seenData || !seenFormat || length == 0 || length % 2 != 0)
                    throw Invalid("Expected one frame-aligned nonempty WAV data chunk after the format.");
                seenData = true;
                if (length > MaximumDurationMs * 32) throw Invalid("Audio fixtures must be at most 15 seconds long.");
                pcm = chunk.ToArray();
            }
            offset = checked((int)paddedEnd);
        }
        if (!seenFormat || pcm is null) throw Invalid("WAV format or audio data is missing.");
        var frames = pcm.LongLength / 2;
        return new AudioFixture(snapshotPath,
            new AudioFixtureInfo(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), frames / 16d, 16_000, 1, 16, frames), pcm);
    }

    private static AudioInjectionException Invalid(string message) => new("invalid-audio", message);
}
