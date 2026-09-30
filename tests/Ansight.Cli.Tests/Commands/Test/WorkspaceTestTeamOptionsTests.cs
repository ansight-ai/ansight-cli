namespace Ansight.Cli.Tests.Commands.Test;

public sealed class WorkspaceTestTeamOptionsTests
{
    [Fact]
    public void ResolveReturnsNullWithoutTeamOption()
    {
        var arguments = CliArguments.Parse(["test", "run", "/workspace", "test-id"]);

        var result = WorkspaceTestTeamOptions.Resolve(arguments);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveReturnsTeamIdentifier()
    {
        var teamId = Guid.NewGuid();
        var arguments = CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", "--team-id", teamId.ToString("D")]);

        var result = WorkspaceTestTeamOptions.Resolve(arguments);

        Assert.Equal(teamId, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ResolveRejectsInvalidTeamIdentifier(string value)
    {
        var arguments = CliArguments.Parse(
            ["test", "run", "/workspace", "test-id", $"--team-id={value}"]);

        var exception = Assert.Throws<CliUsageException>(() => WorkspaceTestTeamOptions.Resolve(arguments));

        Assert.Contains("non-empty GUID", exception.Message, StringComparison.Ordinal);
    }
}
