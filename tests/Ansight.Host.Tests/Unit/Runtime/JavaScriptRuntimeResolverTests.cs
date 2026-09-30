using Ansight.Host.Runtime.Automation;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class JavaScriptRuntimeResolverTests
{
    [Fact]
    public void Resolve_FindsNodeInAPlatformFallbackLocationWhenTheInheritedPathDoesNotContainIt()
    {
        using var environment = TestDirectory.Create();
        var executablePath = Path.Combine(environment.Path, "node");
        File.WriteAllText(executablePath, string.Empty);

        var result = JavaScriptRuntimeResolver.Resolve(
            "node",
            inheritedPath: string.Empty,
            platformFallbackPaths: [executablePath]);

        Assert.True(result.IsAvailable);
        Assert.Equal(executablePath, result.ExecutablePath);
    }

    [Fact]
    public void Resolve_WithAnUnavailableExplicitPath_ReturnsActionableAvailabilityState()
    {
        using var environment = TestDirectory.Create();
        var executablePath = Path.Combine(environment.Path, "missing-node");

        var result = JavaScriptRuntimeResolver.Resolve(executablePath);

        Assert.False(result.IsAvailable);
        Assert.Equal(executablePath, result.ExecutablePath);
        Assert.Contains(nameof(RuntimeOptions.JavaScriptExecutablePath), result.Message, StringComparison.Ordinal);
    }
}
