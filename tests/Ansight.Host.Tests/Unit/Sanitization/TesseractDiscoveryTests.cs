using Ansight.Host.Diagnostics;

namespace Ansight.Host.Tests.Unit.Sanitization;

public sealed class TesseractDiscoveryTests
{
    [Fact]
    public void ScannerFindsInstalledToolsWhenTheResidentHostPathIsEmpty()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalOverride = Environment.GetEnvironmentVariable("ANSIGHT_TESSERACT_PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            Environment.SetEnvironmentVariable("ANSIGHT_TESSERACT_PATH", null);
            var installed = DiagnosticProcess.FindExecutables("tesseract").FirstOrDefault();

            Assert.Equal(installed, TesseractSessionScreenshotOcrScanner.ResolveExecutablePath(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("ANSIGHT_TESSERACT_PATH", originalOverride);
        }
    }

    [Fact]
    public void MissingExplicitOverrideDoesNotSilentlyUseAnotherExecutable()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-tesseract-{Guid.NewGuid():N}");

        Assert.Null(TesseractSessionScreenshotOcrScanner.ResolveExecutablePath(missing));
    }

    [Fact]
    public void ExplicitExecutableTakesPrecedenceOverDiscovery()
    {
        var configured = Path.GetTempFileName();

        try
        {
            Assert.Equal(configured, TesseractSessionScreenshotOcrScanner.ResolveExecutablePath(configured));
        }
        finally
        {
            File.Delete(configured);
        }
    }
}
