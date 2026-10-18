using Ansight.Adb;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class ScrcpyToolLocatorTests
{
    [Fact]
    public void ResolveServerPath_FindsWindowsArchiveLayoutBesideExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ansight-scrcpy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "scrcpy.exe");
            var server = Path.Combine(directory, "scrcpy-server");
            File.WriteAllText(executable, "test executable");
            File.WriteAllText(server, "test server");
            Assert.Equal(server, ScrcpyToolLocator.ResolveServerPath(executable, configuredServer: null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("scrcpy 3.3.1 <https://github.com/Genymobile/scrcpy>", "3.3.1")]
    [InlineData("scrcpy 4.1\nDependencies:", "4.1")]
    [InlineData("SCRCPY 4.0-dev.1", "4.0-dev.1")]
    public void ParseVersion_AcceptsOfficialVersionOutput(string output, string expected)
        => Assert.Equal(expected, ScrcpyToolLocator.ParseVersion(output));

    [Theory]
    [InlineData("")]
    [InlineData("scrcpy")]
    [InlineData("version unknown")]
    public void ParseVersion_RejectsUnrecognizedOutput(string output)
        => Assert.Null(ScrcpyToolLocator.ParseVersion(output));

    [Fact]
    public void GetCandidatePaths_PrefersConfiguredExecutable()
    {
        var configured = Path.Combine(Path.DirectorySeparatorChar.ToString(), "tools", "scrcpy");

        var firstCandidate = ScrcpyToolLocator.GetCandidatePaths(configured).First();

        Assert.Equal(configured, firstCandidate);
    }
}
