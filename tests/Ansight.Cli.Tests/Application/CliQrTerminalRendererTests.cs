namespace Ansight.Cli.Tests.Application;

public sealed class CliQrTerminalRendererTests
{
    [Fact]
    public void RenderUsesHalfBlockRowsAndPreservesQuietZone()
    {
        var rendered = CliQrTerminalRenderer.Render("ans2:terminal-qr-test", useAnsiColors: false);
        var lines = rendered.Split(Environment.NewLine);

        Assert.True(lines.Length > 10);
        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
        Assert.All(lines.Take(2), line => Assert.All(line, character => Assert.Equal(' ', character)));
        Assert.Contains(rendered, character => character is '\u2588' or '\u2580' or '\u2584');
    }

    [Fact]
    public void RenderUsesExplicitBlackOnWhiteTerminalColors()
    {
        var rendered = CliQrTerminalRenderer.Render("ans2:terminal-qr-test");

        Assert.StartsWith("\u001b[30;47m", rendered, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", rendered, StringComparison.Ordinal);
    }
}
