using Ansight.Cli.Auth;
using Ansight.Cli.Commands.Doctor;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class DoctorFullCommandTests
{
    [Theory]
    [InlineData("doctor")]
    [InlineData("capabilities")]
    public void FullReportsSkipPostCommandCredentialReadsAndUpdateChecks(string command)
    {
        var arguments = CliArguments.Parse([command, "--full"]);
        Assert.False(CliApplication.ShouldInitializeAnalytics(arguments));
        Assert.False(CliApplication.ShouldSuggestUpdate(arguments));
    }

    [Theory]
    [InlineData("--include-secret-metadata")]
    [InlineData("--output=report.json")]
    [InlineData("--include-secret-metadata=false")]
    public async Task FullOnlyOptionsFailBeforeCollecting(string option)
    {
        var arguments = CliArguments.Parse(["doctor", option]);
        await Assert.ThrowsAsync<CliUsageException>(() => DoctorCommand.RunAsync(arguments, new CliOutput(true, new StringWriter()), default));
    }

    [Fact]
    public async Task FalseValueCannotEnableSecretMetadataInFullReport()
    {
        var arguments = CliArguments.Parse(["doctor", "--full", "--include-secret-metadata=false"]);
        await Assert.ThrowsAsync<CliUsageException>(() => DoctorCommand.RunAsync(arguments, new CliOutput(true, new StringWriter()), default));
    }

    [Fact]
    public void SecretOptInIsIndependentOfOtherReportOptions()
    {
        var defaults = CliArguments.Parse(["doctor", "--full", "--json", "--verbose", "--output", "report.json"]);
        Assert.False(defaults.HasFlag("include-secret-metadata"));
        Assert.True(CliArguments.Parse(["doctor", "--full", "--include-secret-metadata"]).HasFlag("include-secret-metadata"));
    }

    [Theory]
    [InlineData("doctor")]
    public void RecoveryCommandsDoNotRequirePaidProductAccess(string command)
        => Assert.Equal(CliAccessClass.Bootstrap, CliCommandAccessPolicy.Classify(CliArguments.Parse([command])));
}
