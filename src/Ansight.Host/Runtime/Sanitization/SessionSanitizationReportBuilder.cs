using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed class SessionSanitizationReportBuilder
{
    private readonly Dictionary<string, int> redactionsByRule = new(StringComparer.Ordinal);

    public SessionSanitizationReportBuilder(string policyId)
    {
        PolicyId = policyId;
    }

    public string PolicyId { get; }
    public int StringsScanned { get; set; }
    public int RedactedStrings { get; private set; }
    public int SensitivePropertiesRedacted { get; set; }
    public int ScreenshotsRedacted { get; set; }
    public int ScreenshotsRemoved { get; set; }
    public int ScreenshotRegionsRedacted { get; set; }
    public int ArtifactsSanitized { get; set; }
    public int ArtifactsExcluded { get; set; }

    public void RecordRedaction(string ruleId, int count = 1)
    {
        RedactedStrings++;
        redactionsByRule[ruleId] = redactionsByRule.GetValueOrDefault(ruleId) + count;
    }

    public SessionSanitizationReport Build()
    {
        return new SessionSanitizationReport
        {
            PolicyId = PolicyId,
            SanitizedAtUtc = DateTimeOffset.UtcNow,
            StringsScanned = StringsScanned,
            RedactedStrings = RedactedStrings,
            SensitivePropertiesRedacted = SensitivePropertiesRedacted,
            ScreenshotsRedacted = ScreenshotsRedacted,
            ScreenshotsRemoved = ScreenshotsRemoved,
            ScreenshotRegionsRedacted = ScreenshotRegionsRedacted,
            ArtifactsSanitized = ArtifactsSanitized,
            ArtifactsExcluded = ArtifactsExcluded,
            RedactionsByRule = new Dictionary<string, int>(redactionsByRule, StringComparer.Ordinal)
        };
    }
}
