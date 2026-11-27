using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.Host.Devices;

internal static partial class AndroidDisplayViewport
{
    public static bool TryRead(string source, out HeadlessAndroidDisplaySize size)
    {
        foreach (var line in source.Split('\n'))
        {
            if (!line.Contains("Viewport", StringComparison.Ordinal)
                || line.Contains("isActive=[0]", StringComparison.Ordinal))
            {
                continue;
            }

            var match = DefaultViewportPattern().Match(line);
            var width = 0;
            var height = 0;
            var hasValidDimensions = match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out width)
                && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out height)
                && width > 0
                && height > 0;

            if (hasValidDimensions)
            {
                size = new HeadlessAndroidDisplaySize(width, height);
                return true;
            }
        }

        size = default;
        return false;
    }

    [GeneratedRegex(@"\bdisplayId=0,.*\blogicalFrame=\[0,\s*0,\s*(\d+),\s*(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultViewportPattern();
}
