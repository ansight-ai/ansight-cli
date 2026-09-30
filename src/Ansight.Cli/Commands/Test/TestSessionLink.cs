using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestSessionLink(
    string Kind,
    string SessionId,
    string? TestId,
    string? AppId)
{
    public static IReadOnlyList<TestSessionLink> From(
        WorkspaceTestRunResult result,
        string? testId)
    {
        var links = new List<TestSessionLink>(2);
        if (!string.IsNullOrWhiteSpace(result.SessionId))
        {
            links.Add(new TestSessionLink(
                "ansight",
                result.SessionId,
                testId,
                result.Test?.AppId));
        }

        if (!string.IsNullOrWhiteSpace(result.AppiumSessionId))
        {
            links.Add(new TestSessionLink(
                "appium",
                result.AppiumSessionId,
                testId,
                result.Test?.AppId));
        }

        return links;
    }
}
