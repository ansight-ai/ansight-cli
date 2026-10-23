using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.MacSimulatorHid;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class PenInputTests
{
    private static JsonObject Sample(int time, string phase, double x = .5, double pressure = 0) => new()
    {
        ["offsetMs"] = time, ["phase"] = phase, ["x"] = x, ["y"] = .5, ["pressure"] = pressure
    };

    [Fact]
    public void CompleteContactAndHoverPreserveValues()
    {
        var down = Sample(50, "down", pressure: .7);
        down["altitudeRadians"] = .4; down["azimuthRadians"] = 2; down["rollRadians"] = -3;
        var parsed = LiveStylusTool.ParseSamples(new JsonArray(Sample(0, "hoverEnter"), down,
            Sample(100, "move", .6, .9), Sample(200, "up"), Sample(250, "hoverExit")));
        Assert.Equal(5, parsed.Count);
        Assert.Equal(.7, parsed[1].Sample.Pressure);
        Assert.Equal(.4, parsed[1].Sample.AltitudeRadians);
        Assert.Equal(-3, parsed[1].Sample.RollRadians);
    }

    [Theory]
    [InlineData("move", "up")]
    [InlineData("down", "down")]
    [InlineData("hoverEnter", "up")]
    [InlineData("down", "hoverExit")]
    public void InvalidTransitionsFailBeforeInjection(string first, string second)
        => Assert.Throws<ArgumentException>(() => LiveStylusTool.ParseSamples(new JsonArray(Sample(0, first), Sample(50, second))));

    [Fact]
    public void BoundsTimingAndNoncontactPressureAreValidated()
    {
        Assert.Throws<ArgumentException>(() => LiveStylusTool.ParseSamples(new JsonArray(Sample(0, "down", -1), Sample(50, "up"))));
        Assert.Throws<ArgumentException>(() => LiveStylusTool.ParseSamples(new JsonArray(Sample(0, "down"), Sample(0, "up"))));
        Assert.Throws<ArgumentException>(() => LiveStylusTool.ParseSamples(new JsonArray(Sample(0, "down"), Sample(30001, "up"))));
        Assert.Throws<ArgumentException>(() => LiveStylusTool.ParseSamples(new JsonArray(Sample(0, "down"), Sample(50, "up", pressure: .2))));
        Assert.Throws<ArgumentException>(() => new MacSimulatorStylusSample(MacSimulatorStylusPhase.Down, double.NaN, 0).Validate());
    }

    [Fact]
    public async Task FilePlaybackValidatesVersionAndConfinesTaskPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-pen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = new JsonObject { ["schema"] = "ansight.pen-input/v1", ["samples"] = new JsonArray(Sample(0, "down"), Sample(100, "up")) };
            await File.WriteAllTextAsync(Path.Combine(root, "sample.pen.json"), document.ToJsonString());
            using var scope = PenInputScope.Push(root);
            var loaded = await PenInputFile.ReadAsync("sample.pen.json", CancellationToken.None);
            Assert.Equal(2, LiveStylusTool.ParseSamples(loaded["samples"]!.AsArray()).Count);
            await Assert.ThrowsAsync<ArgumentException>(() => PenInputFile.ReadAsync("../outside.json", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => PenInputFile.ReadAsync(Path.Combine(root, "sample.pen.json"), CancellationToken.None));
            document["schema"] = "ansight.pen-input/v2";
            Assert.Throws<ArgumentException>(() => PenInputFile.Parse(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
        }
        finally { Directory.Delete(root, true); }
    }
}
