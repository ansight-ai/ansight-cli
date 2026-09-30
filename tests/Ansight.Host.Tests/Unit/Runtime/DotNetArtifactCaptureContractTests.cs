using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DotNetArtifactCaptureContractTests
{
    [Fact]
    public async Task StartStartupCapture_RejectsProjectInput()
    {
        using var environment = new TestEnvironment();
        using var profiling = new DotNetProfilingEngine(environment.ApplicationPaths);
        var projectPath = Path.Combine(environment.RootPath, "Example.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            profiling.StartStartupCaptureAsync(new DotNetStartupCaptureRequest(
                projectPath,
                "com.example.app",
                "emulator-5554",
                TimeSpan.FromSeconds(10))));

        Assert.Contains("Expected an .app, .apk, or .ipa", exception.Message, StringComparison.Ordinal);
        Assert.Empty(profiling.ListCaptures());
    }
}
