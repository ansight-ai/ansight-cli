using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Cli.Commands.Test;

internal static class TestResultExporter
{
    private const string ResultDirectoryName = "workspace-test-results";
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

    public static void Save(string filePath, object result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(result);
        var fullPath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The test result path has no parent directory.");
        Directory.CreateDirectory(directoryPath);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, jsonOptions));
            TryRestrictFilePermissions(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
            TryRestrictFilePermissions(fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
