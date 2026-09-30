using Ansight.Infrastructure.Logging;

namespace Ansight.Cli.Tests.Application;

public sealed class CliLoggingTests
{
    [Theory]
    [InlineData(LogLevel.Verbose, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    public void ShouldWrite_DefaultOutputSuppressesDiagnosticLogs(LogLevel level, bool expected)
    {
        Assert.Equal(expected, CliLogging.ShouldWrite(level, verbose: false));
    }

    [Theory]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Fatal)]
    public void ShouldWrite_DefaultOutputRetainsProblems(LogLevel level)
    {
        Assert.True(CliLogging.ShouldWrite(level, verbose: false));
    }

    [Theory]
    [InlineData(LogLevel.Verbose)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Fatal)]
    public void ShouldWrite_VerboseOutputIncludesEveryLevel(LogLevel level)
    {
        Assert.True(CliLogging.ShouldWrite(level, verbose: true));
    }

    [Theory]
    [InlineData(LogLevel.Verbose)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Fatal)]
    public void ShouldWrite_SilentOutputSuppressesEveryLevel(LogLevel level)
    {
        Assert.False(CliLogging.ShouldWrite(level, verbose: true, silent: true));
    }
}
