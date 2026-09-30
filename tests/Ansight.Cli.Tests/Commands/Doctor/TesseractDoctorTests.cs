namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class TesseractDoctorTests
{
    [Theory]
    [InlineData("tesseract 5.5.1\n leptonica-1.85.0", "5.5.1")]
    [InlineData("Tesseract 4.1.3", "4.1.3")]
    [InlineData("custom-build", "custom-build")]
    public void ParseVersion_ReadsFirstVersionLine(string output, string expected)
    {
        Assert.Equal(expected, TesseractDoctor.ParseVersion(output));
    }

    [Fact]
    public async Task CheckAsync_MissingConfiguredPathExplainsEnvironmentVariable()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-tesseract-{Guid.NewGuid():N}");

        var check = await TesseractDoctor.CheckAsync(
            missingPath,
            string.Empty,
            CancellationToken.None);

        Assert.Equal("tool.tesseract", check.Name);
        Assert.Equal("configured-path-missing", check.Status);
        Assert.False(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Contains("ANSIGHT_TESSERACT_PATH", check.Message, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(missingPath), check.Path);
    }

    [Fact]
    public async Task CheckAsync_MissingOptionalToolProvidesInstallationGuidance()
    {
        var check = await TesseractDoctor.CheckAsync(
            configuredExecutablePath: null,
            inheritedPath: string.Empty,
            CancellationToken.None);

        Assert.Equal("unavailable", check.Status);
        Assert.False(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Contains("Tesseract was not found", check.Message, StringComparison.Ordinal);
        Assert.Contains("ANSIGHT_TESSERACT_PATH", check.Message, StringComparison.Ordinal);
    }
}
