namespace Ansight.Host.Tests.Unit.Utilities;

public sealed class SessionImageArtifactPathTests
{
    [Fact]
    public void ResolveSessionDirectoryPath_SanitizesAppAndSessionSegments()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "Ansight Captures");

        var result = SessionImageArtifactPath.ResolveSessionDirectoryPath(
            rootPath,
            " My App/Build ",
            " Session One ");

        Assert.Equal(Path.Combine(rootPath, "my_app_build", "session_one"), result);
    }
}
