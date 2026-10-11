using System.Text.Json;
using Ansight.Host.Diagnostics;
using Ansight.Infrastructure.Dependencies;

namespace Ansight.Host.Tests.Unit.Diagnostics;

public sealed class DependencyBaselineTests
{
    [Fact]
    public void BaselineIsEmbeddedWithContentIdentityAndSetupProvenance()
    {
        var baseline = DependencyBaseline.Bundled;
        Assert.Equal("embedded", baseline.Status);
        Assert.Matches("^[a-f0-9]{64}$", baseline.Sha256!);
        Assert.Equal("setup-observation", baseline.Manifest!.Basis);
        Assert.Contains("Ansight.DependencyBaseline.json", typeof(DependencyBaseline).Assembly.GetManifestResourceNames());
        Assert.Contains(baseline.Manifest.Dependencies, dependency => dependency.Id == "scrcpy");
        Assert.Equal(baseline.Manifest.Dependencies.Count, baseline.Manifest.Dependencies.Select(dependency => dependency.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("2.9.9", "below-minimum", true)]
    [InlineData("3.0", "within-range", false)]
    [InlineData("3.3.1", "at-target", false)]
    [InlineData("3.3.1+package.2", "at-target", false)]
    [InlineData("3.10.0", "within-range", false)]
    [InlineData("4.0", "above-maximum", true)]
    [InlineData("4.1.0", "above-maximum", true)]
    [InlineData(null, "unknown", true)]
    [InlineData("not a version", "unknown", true)]
    [InlineData("999999999999999999999.0", "unknown", true)]
    public void RangeComparisonsProduceAdviceOnly(string? installed, string status, bool notice)
    {
        var advice = DependencyBaseline.Compare(Definition(new("3.0.0", "3.3.1", "4.0.0")), installed);
        Assert.Equal(status, advice.Status);
        Assert.Equal(notice, advice.Notice is not null);
        Assert.DoesNotContain("unsupported", advice.Notice ?? "", StringComparison.OrdinalIgnoreCase);
        if (status is "below-minimum" or "above-maximum") Assert.Contains("will likely still work", advice.Notice);
    }

    [Fact]
    public void MissingMaximumDoesNotUseTargetAsACeiling()
    {
        var advice = DependencyBaseline.Compare(Definition(new("3.0", "3.3.1")), "99.0");
        Assert.Equal("within-range", advice.Status);
        Assert.Null(advice.Notice);
    }

    [Theory]
    [InlineData("3.0.0-rc.1", "below-minimum")]
    [InlineData("3.0.0-rc.2", "within-range")]
    [InlineData("3.0.0-rc.9", "within-range")]
    [InlineData("3.0.0-rc.10", "at-target")]
    [InlineData("3.0.0", "above-maximum")]
    public void PrereleasesUseNumericIdentifierOrdering(string installed, string expected)
        => Assert.Equal(expected, DependencyBaseline.Compare(
            Definition(new("3.0.0-rc.2", "3.0.0-rc.10", "3.0.0")), installed).Status);

    [Fact]
    public void AndroidPackageBuildSuffixIsNotAPrerelease()
    {
        var definition = Definition(new("37.0", "37.0.0")) with { Id = "adb" };
        var advice = DependencyBaseline.Compare(definition, "37.0.0-14910828");
        Assert.Equal("at-target", advice.Status);
        Assert.Equal("37.0.0-14910828", advice.DetectedVersion);
    }

    [Fact]
    public void FullReportReadinessDoesNotChangeForAnOlderDependency()
    {
        var tool = new DiagnosticTool("scrcpy", "available", "0.1", "/bin/scrcpy", "/bin/scrcpy",
            "configuration", "/bin", null, [], "Android video");
        Assert.Equal("available", tool.Status);
        Assert.Equal("below-minimum", tool.VersionAdvice!.Status);
        var section = new SystemReportSection("complete", JsonSerializer.SerializeToNode(tool));
        var report = new SystemReport("ansight.system-report/v1", DateTimeOffset.UnixEpoch, false,
            new Dictionary<string, SystemReportSection> { ["dependencies"] = section }, []);
        Assert.True(report.IsComplete);
        Assert.Contains("will likely still work", SystemReportRenderer.Render(report));
        Assert.Null((tool with { Status = "missing" }).VersionAdvice);
    }

    private static DependencyDefinition Definition(DependencyVersionPolicy versions)
        => new("example", versions, ["macos", "linux", "windows"], "Tests", new Dictionary<string, string>());
}
