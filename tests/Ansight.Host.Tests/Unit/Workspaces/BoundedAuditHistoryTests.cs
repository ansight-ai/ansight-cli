using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class BoundedAuditHistoryTests
{
    [Fact]
    public void Prune_RemovesOldRunAndItsMeteringAndTraceFiles()
    {
        using var directory = TestDirectory.Create();
        var oldPath = Path.Combine(directory.Path, "old.json");
        var currentPath = Path.Combine(directory.Path, "current.json");
        File.WriteAllText(oldPath, "{}");
        File.WriteAllText(oldPath + ".metering", "[]");
        var tracePath = Path.Combine(directory.Path, "old.trace");
        Directory.CreateDirectory(tracePath);
        File.WriteAllText(Path.Combine(tracePath, "ocr.json"), "{}");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-1));
        for (var index = 0; index < BoundedAuditHistory.MaximumRuns; index++)
        {
            File.WriteAllText(Path.Combine(directory.Path, $"run-{index:D4}.json"), "{}");
        }

        File.WriteAllText(currentPath, "{}");
        BoundedAuditHistory.Prune(directory.Path, currentPath, includeTraceDirectories: true);

        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(oldPath + ".metering"));
        Assert.False(Directory.Exists(tracePath));
        Assert.True(File.Exists(currentPath));
        Assert.Equal(BoundedAuditHistory.MaximumRuns,
            Directory.EnumerateFiles(directory.Path, "*.json").Count());
    }
}
