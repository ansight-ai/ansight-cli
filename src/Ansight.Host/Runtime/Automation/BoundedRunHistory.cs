using System.Text;

namespace Ansight.Host.Runtime.Automation;

internal static class BoundedRunHistory
{
    internal const long MaximumFileBytes = 16 * 1_024 * 1_024;
    internal const int MaximumRecordBytes = 1_024 * 1_024;

    internal static void Compact<T>(
        string filePath,
        IEnumerable<T> records,
        Func<T, DateTimeOffset> completedAtUtc,
        Func<T, string> serialize,
        int maximumRecords)
    {
        var lines = new List<string>();
        long totalBytes = 0;
        foreach (var record in records.OrderByDescending(completedAtUtc))
        {
            if (lines.Count >= maximumRecords)
            {
                break;
            }

            var line = serialize(record);
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (bytes > MaximumRecordBytes || totalBytes + bytes > MaximumFileBytes)
            {
                continue;
            }

            lines.Add(line);
            totalBytes += bytes;
        }

        lines.Reverse();
        var temporaryPath = filePath + ".tmp";
        File.WriteAllLines(temporaryPath, lines);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    internal static string LimitText(string value, int maximumCharacters)
        => value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + " [truncated]";

    internal static void RestrictFile(string filePath)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(filePath))
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
