using Ansight.Host.Runtime;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Infrastructure;

public sealed class ProtocolDependencyBoundaryTests
{
    [Fact]
    public void HostReferencesProtocolWithoutTheAppSdkRuntime()
    {
        var dependencies = typeof(RuntimeCoordinator).Assembly.GetReferencedAssemblies();
        Assert.Contains(dependencies, dependency => dependency.Name == "Ansight.Protocol");
        Assert.DoesNotContain(dependencies, dependency => dependency.Name is "Ansight" or "Ansight.Core" or "Ansight.Build"
            || dependency.Name?.StartsWith("Ansight.Native.", StringComparison.Ordinal) == true);
        Assert.Equal("Ansight.Protocol", typeof(ToolProtocolEnvelope).Assembly.GetName().Name);
    }
}
