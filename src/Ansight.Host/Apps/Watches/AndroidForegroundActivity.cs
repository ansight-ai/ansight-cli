using System.Text.RegularExpressions;

namespace Ansight.Host.Apps;

internal static class AndroidForegroundActivity
{
    private static readonly Regex resumedActivity = new(
        @"(?:topResumedActivity\s*=|mResumedActivity\s*:|mResumedActivity\s*=)[^\r\n]*?\b(?<package>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+)/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsForeground(string activities, string appId)
    {
        var match = resumedActivity.Match(activities);
        if (!match.Success)
            throw new IOException("ADB did not expose a resumed Android activity; foreground state could not be verified.");
        return string.Equals(match.Groups["package"].Value, appId, StringComparison.Ordinal);
    }
}
