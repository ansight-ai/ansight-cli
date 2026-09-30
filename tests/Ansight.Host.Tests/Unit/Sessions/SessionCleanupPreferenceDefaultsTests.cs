using Ansight.Infrastructure.Preferences;

namespace Ansight.Host.Tests.Unit.Sessions;

public sealed class SessionCleanupPreferenceDefaultsTests
{
    [Fact]
    public void Defaults_CompactAfterThirtyDaysAndRetainForNinetyDays()
    {
        Assert.Equal(30, SessionCleanupPreferenceDefaults.CompactionAgeDays);
        Assert.Equal(90, SessionCleanupPreferenceDefaults.RetentionDays);
    }
}
