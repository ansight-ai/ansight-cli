using Ansight.Adb;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class ScrcpyDoctorTests
{
    [Fact]
    public void CreateScrcpyCheck_ReportsValidatedToolAsAvailable()
    {
        var resolution = ScrcpyToolResolution.Found(
            "/tools/scrcpy",
            "/tools/scrcpy-server",
            "3.3.1",
            "PATH");

        var check = DoctorCommand.CreateScrcpyCheck(resolution);

        Assert.Equal("tool.scrcpy", check.Name);
        Assert.Equal("available", check.Status);
        Assert.True(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Equal("/tools/scrcpy", check.Path);
        Assert.Contains("3.3.1", check.Message, StringComparison.Ordinal);
        Assert.Contains("/tools/scrcpy-server", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateScrcpyCheck_ReportsMissingToolAsOptionalWarning()
    {
        var resolution = ScrcpyToolResolution.NotFound(
            "scrcpy could not be found. Install it with 'brew install scrcpy'.");

        var check = DoctorCommand.CreateScrcpyCheck(resolution);

        Assert.Equal("tool.scrcpy", check.Name);
        Assert.Equal("unavailable", check.Status);
        Assert.False(check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Null(check.Path);
        Assert.Contains("brew install scrcpy", check.Message, StringComparison.Ordinal);
    }
}
