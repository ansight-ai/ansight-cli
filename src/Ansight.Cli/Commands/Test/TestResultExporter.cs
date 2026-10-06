using System.Text.Json;
using System.Text.Json.Serialization;
using Ansight.Infrastructure;

namespace Ansight.Cli.Commands.Test;

internal static class TestResultExporter
{
    private const string ResultDirectoryName = "workspace-test-results";
    private const int MaximumGeneratedResults = 100;
    private const long MaximumGeneratedResultBytes = 256L * 1_024 * 1_024;
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string CreatePath(
        string dataDirectory,
        string? requestedPath,
        string resultName,
        string? runId = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath.Trim()));
        }

        var safeName = SanitizeFileName(resultName);
        var safeRunId = string.IsNullOrWhiteSpace(runId)
            ? Guid.NewGuid().ToString("N")
            : SanitizeFileName(runId);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{safeName}-{safeRunId}.json";
        return Path.Combine(dataDirectory, "data", ResultDirectoryName, fileName);
    }

    public static void Save(string filePath, object result, bool pruneGeneratedResults = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(result);
        var fullPath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The test result path has no parent directory.");
        if (pruneGeneratedResults)
        {
            PrivateStorageDirectory.Ensure(directoryPath);
        }
        else
        {
            Directory.CreateDirectory(directoryPath);
        }
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, jsonOptions));
            TryRestrictFilePermissions(temporaryPath);
            if (pruneGeneratedResults && new FileInfo(temporaryPath).Length > MaximumGeneratedResultBytes)
            {
                throw new InvalidDataException("The generated test result exceeds the local result cache limit.");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
            TryRestrictFilePermissions(fullPath);
            if (pruneGeneratedResults)
            {
                PruneGeneratedResults(directoryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PruneGeneratedResults(string directoryPath, string currentFilePath)
    {
        var files = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ToArray();
        var totalBytes = files.Sum(static file => file.Length);
        var remainingCount = files.Length;
        for (var index = files.Length - 1; index >= 0; index--)
        {
            if (remainingCount <= MaximumGeneratedResults && totalBytes <= MaximumGeneratedResultBytes)
            {
                break;
            }

            var file = files[index];
            if (string.Equals(file.FullName, currentFilePath, StringComparison.Ordinal))
            {
                continue;
            }

            totalBytes -= file.Length;
            file.Delete();
            remainingCount--;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var normalized = new string(value.Trim()
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "test" : normalized;
    }

    private static void TryRestrictFilePermissions(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
        }
    }
}
