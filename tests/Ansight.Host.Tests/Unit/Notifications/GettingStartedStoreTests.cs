using Ansight.Host.Explorer;

namespace Ansight.Host.Tests.Unit.Notifications;

public sealed class GettingStartedStoreTests
{
    [Fact]
    public void ProgressAndSkipSurviveStoreReload()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new GettingStartedStore(environment.ApplicationPaths.ApplicationDataPath);

        Assert.False(store.Read().Opened);
        store.Update(state => state with { Opened = true, CapturePath = "sdk", ReplayedSessionId = "session-1" });
        store.Update(state => state with { Skipped = true });

        var reloaded = new GettingStartedStore(environment.ApplicationPaths.ApplicationDataPath).Read();
        Assert.True(reloaded.Opened);
        Assert.True(reloaded.Skipped);
        Assert.Equal("sdk", reloaded.CapturePath);
        Assert.Equal("session-1", reloaded.ReplayedSessionId);
    }
}
