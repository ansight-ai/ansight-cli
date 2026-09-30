namespace Ansight.Host.Tests.TestSupport;

internal static class TestWait
{
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        string? because = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var startedUtc = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedUtc < effectiveTimeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), because ?? "Timed out waiting for the expected condition.");
    }
}
