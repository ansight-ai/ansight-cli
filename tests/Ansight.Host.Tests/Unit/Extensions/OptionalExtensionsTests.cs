using Ansight.Infrastructure.Extensions;
namespace Ansight.Host.Tests.Unit.Extensions;
public sealed class OptionalExtensionsTests
{
    [Fact]
    public void RegistrationDoesNotConstructModuleAndExplicitInvocationCreatesItOnce()
    {
        using var temporary = new Ansight.Host.Tests.Unit.Runtime.TemporaryDirectory();
        OptionalExtensions.Register("Test.Module", context => new InertOptionalExtension(context));
        var counters = new Dictionary<string, int>();
        using (var extensions = new OptionalExtensions(counters, temporary.RootPath))
        {
            Assert.True(extensions.IsAvailable("Test.Module"));
            Assert.Empty(extensions.ReadUiEntryPoints("Test.Module"));
            Assert.Null(extensions.TryGetLoadedService<IDisposable>("Test.Module"));
            Assert.Empty(counters);
            var first = extensions.GetService<IDisposable>("Test.Module");
            Assert.Same(first, extensions.GetService<IDisposable>("Test.Module"));
            Assert.Equal(1, counters["constructed"]);
        }
        Assert.Equal(1, counters["disposed"]);
    }
    [Fact]
    public void MissingModuleDoesNotBlockLocalRuntimeOrAdvertiseOperations()
    {
        using var temporary = new Ansight.Host.Tests.Unit.Runtime.TemporaryDirectory();
        using var extensions = new OptionalExtensions(new object(), temporary.RootPath);
        Assert.False(extensions.IsAvailable());
        Assert.Empty(extensions.ReadOperationDefinitions());
        Assert.Throws<OptionalExtensionUnavailableException>(() => extensions.GetService<IDisposable>());
        Assert.Null(extensions.ResolveUiAsset("api/extensions/Test.Module/../../secret.dll"));
    }
}
public sealed class InertOptionalExtension : IOptionalExtension
{
    private readonly Dictionary<string, int> counters;
    public InertOptionalExtension(object context)
    {
        counters = (Dictionary<string, int>)context;
        counters["constructed"] = counters.GetValueOrDefault("constructed") + 1;
    }
    public object GetService(Type contract) => this;
    public void Dispose() => counters["disposed"] = counters.GetValueOrDefault("disposed") + 1;
}
