using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class DoctorOutputTests
{
    [Theory]
    [InlineData(true, true, "green")]
    [InlineData(false, false, "amber")]
    [InlineData(false, true, "red")]
    public void CheckSignal_UsesTrafficLightSeverity(
        bool isSuccess,
        bool isRequired,
        string expectedSignal)
    {
        var check = CreateCheck("test", isSuccess, isRequired);

        Assert.Equal(expectedSignal, check.Signal);
    }

    [Fact]
    public void ResultSignal_IsAmberWhenOnlyOptionalChecksFail()
    {
        var result = CreateResult([
            CreateCheck("required", true, true),
            CreateCheck("optional", false, false)
        ]);

        Assert.Equal("amber", result.Signal);
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void ResultSignal_IsRedWhenARequiredCheckFails()
    {
        var result = CreateResult([
            CreateCheck("optional", false, false),
            CreateCheck("required", false, true)
        ]);

        Assert.Equal("red", result.Signal);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public void DotNetDiagnosticTools_AreOptionalForTheSelfContainedCli()
    {
        var checks = DoctorCommand.CheckDotNetTools();

        Assert.Equal(
            ["tool.dotnet-trace", "tool.dotnet-dsrouter"],
            checks.Select(static check => check.Name));
        Assert.All(checks, static check => Assert.False(check.IsRequired));
    }

    [Fact]
    public void Render_ShowsTrafficLightsAndAccessibleLabels()
    {
        var result = CreateResult([
            CreateCheck("available", true, true),
            CreateCheck("warning", false, false)
        ]);

        var rendered = DoctorCommand.Render(result);

        Assert.Contains("🟡 Ansight doctor: AMBER", rendered, StringComparison.Ordinal);
        Assert.Contains("🟢 [ok] available", rendered, StringComparison.Ordinal);
        Assert.Contains("🟡 [warn] warning", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutput_IncludesSignalsAndDependencyGuidance()
    {
        var result = CreateResult([
            CreateCheck("tool.node", true, true),
            CreateCheck("tool.tesseract", false, false)
        ]);
        using var standardOutput = new StringWriter();
        var output = new CliOutput(true, standardOutput, TextWriter.Null);

        output.Write(result, () => DoctorCommand.Render(result));

        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.Equal("ansight.doctor/v1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal("amber", document.RootElement.GetProperty("signal").GetString());
        var checks = document.RootElement.GetProperty("checks");
        Assert.Equal("green", checks[0].GetProperty("signal").GetString());
        Assert.Equal("amber", checks[1].GetProperty("signal").GetString());
        Assert.Contains(
            "workspace tasks",
            checks[0].GetProperty("justification").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Node.js LTS",
            checks[0].GetProperty("installInstructions").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "PII redaction",
            checks[1].GetProperty("justification").GetString(),
            StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(
            checks[1].GetProperty("installInstructions").GetString()));
        Assert.DoesNotContain("🟡", standardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCheckPreservesNativeCompatibilityStatus()
    {
        var check = DoctorCommand.CreateDeviceCheck(new DeviceCapability(
            "ios.simulator-hid",
            false,
            "/Applications/Xcode-beta.app/Contents/Developer",
            "The selected Xcode is incompatible.",
            "xcode-incompatible"));

        Assert.Equal("device.ios.simulator-hid", check.Name);
        Assert.Equal(OperatingSystem.IsMacOS() ? "xcode-incompatible" : "not-applicable", check.Status);
        Assert.Equal(!OperatingSystem.IsMacOS(), check.IsSuccess);
        Assert.False(check.IsRequired);
        Assert.Equal(OperatingSystem.IsMacOS() ? "amber" : "green", check.Signal);
    }

    [Fact]
    public void RequiredDoctorCheckBecomesReleaseBlocking()
    {
        IList<DoctorCheck> checks =
        [
            CreateCheck("device.ios", true, false),
            CreateCheck("device.ios.simulator-hid", false, false)
        ];

        DoctorCommand.RequireChecks(checks, ["device.ios.simulator-hid"]);

        Assert.False(checks[0].IsRequired);
        Assert.True(checks[1].IsRequired);
        Assert.Equal("red", checks[1].Signal);
    }

    [Fact]
    public void UnknownRequiredDoctorCheckIsRejected()
    {
        IList<DoctorCheck> checks = [CreateCheck("device.ios", true, false)];

        var exception = Assert.Throws<CliUsageException>(() =>
            DoctorCommand.RequireChecks(checks, ["device.ios.simulator-hid"]));

        Assert.Contains("Unknown doctor check name", exception.Message, StringComparison.Ordinal);
    }

    private static DoctorCheck CreateCheck(string name, bool isSuccess, bool isRequired)
        => new(
            name,
            isSuccess ? "available" : "unavailable",
            isSuccess,
            isRequired,
            "Test check.",
            null);

    private static DoctorResult CreateResult(IReadOnlyList<DoctorCheck> checks)
        => new(
            "ansight.doctor/v1",
            "Test OS",
            "arm64",
            "10.0.0",
            "/tmp/ansight",
            checks,
            checks.All(static check => check.IsSuccess || !check.IsRequired),
            DateTimeOffset.UnixEpoch);
}
