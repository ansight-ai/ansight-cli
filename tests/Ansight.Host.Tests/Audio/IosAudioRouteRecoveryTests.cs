using Ansight.Host.Audio;
using Ansight.Host.Audio.Ios;

namespace Ansight.Host.Tests.Audio;

[Collection("IosAudioRecovery")]
public sealed class IosAudioRouteRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ansight-route-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AudioTarget target = new("session", "app", Guid.NewGuid().ToString(), "ios");

    [Fact]
    public async Task ConfirmedMismatchRestartsOnceOnNextMatchingLaunchAndSurvivesHostRecreation()
    {
        new IosAudioRouteRecovery(directory).Record(target);
        var recovery = new IosAudioRouteRecovery(directory);
        var restarts = 0;

        Assert.True(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddHours(-1)),
            _ => { restarts++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => throw new Exception("Completed recovery must not inspect or restart again."),
            _ => throw new Exception("Completed recovery must not restart again."), CancellationToken.None));
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task HealthyOrUnrelatedLaunchDoesNotRestart()
    {
        var recovery = new IosAudioRouteRecovery(directory);
        Task<DateTimeOffset?> UnexpectedProbe(CancellationToken _) => throw new Exception("Unrelated launch inspected recovery.");
        Task UnexpectedRestart(CancellationToken _) => throw new Exception("Unrelated launch restarted a simulator.");

        Assert.False(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            UnexpectedProbe, UnexpectedRestart, CancellationToken.None));
        recovery.Record(target);
        Assert.False(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, "another-app",
            UnexpectedProbe, UnexpectedRestart, CancellationToken.None));
        Assert.False(await recovery.RecoverBeforeLaunchAsync(Guid.NewGuid().ToString(), target.AppId,
            UnexpectedProbe, UnexpectedRestart, CancellationToken.None));
    }

    [Fact]
    public async Task ManualRestartAlreadyCompletedDoesNotRestartAgain()
    {
        var recovery = new IosAudioRouteRecovery(directory);
        recovery.Record(target);
        Assert.False(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddMinutes(1)),
            _ => throw new Exception("Manual recovery must be respected."), CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedRecoveryRemainsPending()
    {
        var recovery = new IosAudioRouteRecovery(directory);
        recovery.Record(target);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(null),
            _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));

        Assert.True(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(null), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task FailedRecoveryRemainsPending()
    {
        var recovery = new IosAudioRouteRecovery(directory);
        recovery.Record(target);
        await Assert.ThrowsAsync<IOException>(() => recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(null), _ => throw new IOException("Boot failed."), CancellationToken.None));
        Assert.True(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(null), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task ActiveInjectionBlocksRestartWithoutConsumingRequirement()
    {
        var recovery = new IosAudioRouteRecovery(directory);
        recovery.Record(target);
        using (AudioRouteLease.Acquire("ios:simulator-audio-route"))
        {
            var error = await Assert.ThrowsAsync<AudioInjectionException>(() => recovery.RecoverBeforeLaunchAsync(
                target.DeviceId, target.AppId, _ => throw new Exception("Must not inspect while route is busy."),
                _ => throw new Exception("Must not restart while route is busy."), CancellationToken.None));
            Assert.Equal("audio-route-busy", error.Code);
        }
        Assert.True(await recovery.RecoverBeforeLaunchAsync(target.DeviceId, target.AppId,
            _ => Task.FromResult<DateTimeOffset?>(null), _ => Task.CompletedTask, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
