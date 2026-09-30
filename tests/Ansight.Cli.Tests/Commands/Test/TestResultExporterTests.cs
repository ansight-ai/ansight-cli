using System.Text.Json;

namespace Ansight.Cli.Tests.Commands.Test;

public sealed class TestResultExporterTests
{
    [Fact]
    public void CreatePathAndSaveWriteAProtectedJsonResult()
    {
        using var directory = TestDirectory.Create();
        var resultPath = TestResultExporter.CreatePath(
            directory.Path,
            requestedPath: null,
            "map smoke/test",
            "run-123");

        TestResultExporter.Save(resultPath, new { schema = "ansight.test/v1", exitCode = 10 });

        Assert.StartsWith(
            Path.Combine(directory.Path, "data", "workspace-test-results"),
            resultPath,
            StringComparison.Ordinal);
        Assert.EndsWith("-map-smoke-test-run-123.json", resultPath, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        Assert.Equal("ansight.test/v1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(10, document.RootElement.GetProperty("exitCode").GetInt32());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(resultPath));
        }
    }

    [Fact]
    public void CreatePathUsesTheRequestedResultFile()
    {
        using var directory = TestDirectory.Create();
        var requestedPath = Path.Combine(directory.Path, "ci", "result.json");

        var resultPath = TestResultExporter.CreatePath(
            directory.Path,
            requestedPath,
            "ignored");

        Assert.Equal(Path.GetFullPath(requestedPath), resultPath);
    }
}
