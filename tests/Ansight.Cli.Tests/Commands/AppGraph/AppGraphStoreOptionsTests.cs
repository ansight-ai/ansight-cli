using Ansight.Cli.Commands.AppGraph;

namespace Ansight.Cli.Tests.Commands.AppGraph;

public sealed class AppGraphStoreOptionsTests
{
    [Theory]
    [InlineData("app-graph", "list")]
    [InlineData("app-graph", "explore")]
    [InlineData("app", "execute", "--app-graph")]
    public void LocalFeaturesDefaultToLocalStorage(params string[] command)
        => Assert.Equal(AppGraphStoreMode.Local, AppGraphStoreOptions.Resolve(CliArguments.Parse(command)));

    [Theory]
    [InlineData("cloud", "app-graph", "list")]
    [InlineData("cloud", "app-graphs", "show")]
    [InlineData("app-graph", "list", "--graph-store", "hosted")]
    [InlineData("app", "execute", "--graph-store", "server")]
    public void HostedStorageRequiresExplicitSelection(params string[] command)
        => Assert.Equal(AppGraphStoreMode.Hosted, AppGraphStoreOptions.Resolve(CliArguments.Parse(command)));

    [Fact]
    public void ExplicitLocalStorageIsNotSilentlyOverriddenByCloudAlias()
        => Assert.Equal(AppGraphStoreMode.Local,
            AppGraphStoreOptions.Resolve(CliArguments.Parse(["cloud", "app-graph", "list", "--graph-store", "local"])));
}
