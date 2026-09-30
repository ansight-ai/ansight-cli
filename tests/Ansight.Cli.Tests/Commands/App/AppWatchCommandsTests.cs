namespace Ansight.Cli.Tests.Commands.App;

public sealed class AppWatchCommandsTests
{
    [Theory]
    [InlineData("app watch add test.app", null)]
    [InlineData("app watch add test.app --screenshot-interval-ms 1000", 1000)]
    [InlineData("app watch configure watch-id --screenshot-interval-ms 100", 100)]
    [InlineData("app watch configure watch-id --screenshot-interval-ms 60000", 60000)]
    public void ScreenshotIntervalsAcceptValidatedMilliseconds(string args, int? expected)
        => Assert.Equal(expected, Ansight.Cli.Commands.App.AppWatchCommands.ReadScreenshotInterval(CliArguments.Parse(args.Split(' '))));

    [Theory]
    [InlineData("--screenshot-interval-ms")]
    [InlineData("--screenshot-interval-ms nope")]
    [InlineData("--screenshot-interval-ms 99")]
    [InlineData("--screenshot-interval-ms 60001")]
    [InlineData("--screenshot-interval-ms 1000 --screenshot-interval-ms 500")]
    public void InvalidScreenshotIntervalsAreRejected(string options)
        => Assert.Throws<CliUsageException>(() => Ansight.Cli.Commands.App.AppWatchCommands.ReadScreenshotInterval(
            CliArguments.Parse(("app watch add test.app " + options).Split(' '))));

    [Theory]
    [InlineData("app watch add test.app")]
    [InlineData("app watch add test.app --platform ios")]
    [InlineData("app watch add test.app --device-id Phone")]
    public void SelectionFiltersAreOptional(string args)
        => Ansight.Cli.Commands.App.AppWatchCommands.ValidateSelectionOptions(CliArguments.Parse(args.Split(' ')));

    [Theory]
    [InlineData("app watch add test.app --device-id")]
    [InlineData("app watch add test.app --platform")]
    [InlineData("app watch add test.app --device-id one --device-id two")]
    [InlineData("app watch add test.app --platform ios --platform android")]
    public void ExplicitFiltersRequireOneValue(string args)
        => Assert.Throws<CliUsageException>(() => Ansight.Cli.Commands.App.AppWatchCommands.ValidateSelectionOptions(CliArguments.Parse(args.Split(' '))));
}
