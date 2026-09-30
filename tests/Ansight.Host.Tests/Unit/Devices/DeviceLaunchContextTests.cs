namespace Ansight.Host.Tests.Unit.Devices;

public sealed class DeviceLaunchContextTests
{
    [Fact]
    public void NestedScopesRestorePreviousPreference()
    {
        Assert.False(DeviceLaunchContext.Headless);
        using (DeviceLaunchContext.Push(true))
        {
            Assert.True(DeviceLaunchContext.Headless);
            using (DeviceLaunchContext.Push(false))
            {
                Assert.False(DeviceLaunchContext.Headless);
            }

            Assert.True(DeviceLaunchContext.Headless);
        }

        Assert.False(DeviceLaunchContext.Headless);
    }

    [Fact]
    public async Task ConcurrentCommandsDoNotSharePreferences()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headlessCommand = Task.Run(async () =>
        {
            using var scope = DeviceLaunchContext.Push(true);
            ready.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(DeviceLaunchContext.Headless);
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(DeviceLaunchContext.Headless);
            using var visibleCommand = DeviceLaunchContext.Push(false);
            await Task.Yield();
            Assert.False(DeviceLaunchContext.Headless);
        }
        finally
        {
            release.SetResult();
            await headlessCommand;
        }
    }

    [Fact]
    public async Task DetachedWorkRetainsPreferenceAfterCommandScopeEnds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> capture;
        using (DeviceLaunchContext.Push(true))
        {
            capture = Task.Run(async () =>
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return DeviceLaunchContext.Headless;
            });
        }

        Assert.False(DeviceLaunchContext.Headless);
        release.SetResult();
        Assert.True(await capture);
    }
}
