using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LocalSessionSummaryRunnerTests
{
    [Fact]
    public void FormatSummary_RendersDescriptionAndChronologicalSteps()
    {
        const string response = """
            {"sessionDescription":"In this session, the tester opened the map and reached a failed search.","steps":["Opened the Map tab","Searched for \"Bunny Bucket\"","Saw the search error"]}
            """;

        var summary = LocalSessionSummaryRunner.FormatSummary(response);

        Assert.Equal("In this session, the tester opened the map and reached a failed search.\n\n"
            + "1. Opened the Map tab\n2. Searched for \"Bunny Bucket\"\n3. Saw the search error", summary);
    }

    [Fact]
    public void FormatSummary_RejectsMissingSteps()
    {
        const string response = """
            {"sessionDescription":"In this session, the tester opened the map.","steps":[]}
            """;

        Assert.Throws<InvalidOperationException>(() => LocalSessionSummaryRunner.FormatSummary(response));
    }
}
