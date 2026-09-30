using System.Reflection;
using System.Reflection.Emit;
using Ansight.Host.Diagnostics;

namespace Ansight.Host.Tests.Unit.Diagnostics;

public sealed class HostHealthServiceTests
{
    [Fact]
    public void ResolveHostVersion_UsesLowercaseCliEntryAssembly()
    {
        var assembly = CreateAssembly(
            "ansight",
            new AssemblyMetadataAttribute("AnsightCliVersion", "0.34.0"));

        var version = HostHealthService.ResolveHostVersion(assembly);

        Assert.Equal("0.34.0", version);
    }

    [Fact]
    public void ResolveAssemblyProductVersion_PrefersPackagedProductVersion()
    {
        var assembly = CreateAssembly(
            null,
            new AssemblyMetadataAttribute("AnsightCliVersion", "0.34.0"),
            new AssemblyInformationalVersionAttribute("0.34.0+build.2026090200.abcdef"));

        var version = HostHealthService.ResolveAssemblyProductVersion(assembly);

        Assert.Equal("0.34.0", version);
    }

    [Fact]
    public void ResolveAssemblyProductVersion_RemovesBuildMetadataFromInformationalVersion()
    {
        var assembly = CreateAssembly(
            null,
            new AssemblyInformationalVersionAttribute("0.34.0+build.2026090200.abcdef"));

        var version = HostHealthService.ResolveAssemblyProductVersion(assembly);

        Assert.Equal("0.34.0", version);
    }

    private static Assembly CreateAssembly(string? assemblyName, params Attribute[] attributes)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName ?? $"Ansight.HostHealth.Tests.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        foreach (var attribute in attributes)
        {
            var attributeType = attribute.GetType();
            var constructorArguments = attribute switch
            {
                AssemblyMetadataAttribute metadata => new object[] { metadata.Key, metadata.Value ?? string.Empty },
                AssemblyInformationalVersionAttribute informational => new object[] { informational.InformationalVersion },
                _ => throw new ArgumentOutOfRangeException(nameof(attributes), attributeType, "Unsupported assembly attribute.")
            };
            var constructor = attributeType.GetConstructors().Single(candidate =>
                candidate.GetParameters().Length == constructorArguments.Length);
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, constructorArguments));
        }

        return assembly;
    }
}
