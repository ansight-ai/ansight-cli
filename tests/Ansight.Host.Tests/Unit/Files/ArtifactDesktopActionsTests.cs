using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class ArtifactDesktopActionsTests
{
    [Fact]
    public async Task ExportPreservesExistingFilesAndDirectoriesWithConflictingNames()
    {
        using var environment = new TestEnvironment();
        var source = Path.Combine(environment.RootPath, "reference graph.json");
        var desktop = Path.Combine(environment.RootPath, "Desktop");
        Directory.CreateDirectory(desktop);
        await File.WriteAllTextAsync(source, "{\"artifact\":true}");
        var existing = Path.Combine(desktop, "reference graph.json");
        await File.WriteAllTextAsync(existing, "Keep this file");
        Directory.CreateDirectory(Path.Combine(desktop, "reference graph (1).json"));

        var exported = await ArtifactDesktopActions.ExportToDirectoryAsync(source, desktop, CancellationToken.None);

        Assert.Equal(Path.Combine(desktop, "reference graph (2).json"), exported);
        Assert.Equal("Keep this file", await File.ReadAllTextAsync(existing));
        Assert.Equal(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(exported));
    }

    [Fact]
    public async Task ConcurrentExportsProduceCompleteDistinctCopies()
    {
        using var environment = new TestEnvironment();
        var source = Path.Combine(environment.RootPath, "model.glb");
        var content = new byte[1024 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(source, content);
        var desktop = Path.Combine(environment.RootPath, "Desktop");

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ArtifactDesktopActions.ExportToDirectoryAsync(source, desktop, CancellationToken.None)));

        Assert.Equal(paths.Length, paths.Distinct().Count());
        foreach (var path in paths)
        {
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task CancelledExportDoesNotCreateDestination()
    {
        using var environment = new TestEnvironment();
        var source = Path.Combine(environment.RootPath, "artifact.txt");
        await File.WriteAllTextAsync(source, "Artifact");
        var desktop = Path.Combine(environment.RootPath, "Desktop");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArtifactDesktopActions.ExportToDirectoryAsync(source, desktop, cancellation.Token));

        Assert.False(Directory.Exists(desktop));
    }
}
