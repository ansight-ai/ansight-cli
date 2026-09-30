using Ansight.Host.Files;

namespace Ansight.Host.Tests.Unit.Files;

public sealed class ArtifactPlatformApplicationsTests
{
    [Fact]
    public async Task ListAsync_OnMacOS_ReturnsInstalledApplicationsForJson()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = CreateArtifactDirectory();
        try
        {
            var applications = await ArtifactPlatformApplications.ListAsync(Path.Combine(directory, "artifact é.json"), CancellationToken.None);

            Assert.NotEmpty(applications);
            Assert.All(applications, application =>
            {
                Assert.True(Path.IsPathFullyQualified(application.Id));
                Assert.True(Directory.Exists(application.Id));
                Assert.EndsWith(".app", application.Id, StringComparison.OrdinalIgnoreCase);
                Assert.False(string.IsNullOrWhiteSpace(application.Name));
            });
            Assert.Equal(applications.Count, applications.Select(application => application.Id).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_OnMacOS_RejectsUnregisteredApplicationWithoutLaunching()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var directory = CreateArtifactDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ArtifactPlatformApplications.OpenAsync(
                Path.Combine(directory, "artifact é.json"),
                "/not-an-installed-viewer/Ansight test.app",
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsMissingApplicationBeforeAccessingPlatform()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ArtifactPlatformApplications.OpenAsync(
            "nonexistent.json", " ", CancellationToken.None));
    }

    [Fact]
    public async Task ListAndOpenAsync_WhenCanceled_DoNotAccessPlatform()
    {
        var cancellationToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArtifactPlatformApplications.ListAsync(
            "nonexistent.json", cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArtifactPlatformApplications.OpenAsync(
            "nonexistent.json", "unregistered", cancellationToken));
    }

    private static string CreateArtifactDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ansight-artifact-viewers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "artifact é.json"), "{}");
        return directory;
    }
}
