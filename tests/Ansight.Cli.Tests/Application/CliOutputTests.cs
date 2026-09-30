namespace Ansight.Cli.Tests.Application;

public sealed class CliOutputTests
{

    [Fact]
    public void SilentOutputSuppressesEveryOutputChannel()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var output = new CliOutput(
            json: true,
            standardOutput,
            standardError,
            silent: true);

        output.Write(new { value = 1 });
        output.WriteText("text");
        output.WriteError("error", "message", 1);
        output.WriteProgress("progress");
        output.WriteProgressRaw("raw-progress");
        output.WriteRaw("raw-out", "raw-error");

        Assert.Equal(string.Empty, standardOutput.ToString());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void ProgressOutputUsesStandardErrorAndPreservesRawCarriageReturns()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var output = new CliOutput(
            json: true,
            standardOutput,
            standardError);

        output.WriteProgress("[update] Checking for updates...");
        output.WriteProgressRaw("Downloading: 50%\r");
        output.Write(new { schema = "ansight.test/v1" });

        Assert.Equal(
            $"[update] Checking for updates...{Environment.NewLine}Downloading: 50%\r",
            standardError.ToString());
        Assert.Contains("\"schema\": \"ansight.test/v1\"", standardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputSerializesDeepVisualTreePayloads()
    {
        using var standardOutput = new StringWriter();
        var output = new CliOutput(json: true, standardOutput: standardOutput);
        var root = new System.Text.Json.Nodes.JsonObject();
        var current = root;
        for (var index = 0; index < 80; index++)
        {
            var child = new System.Text.Json.Nodes.JsonObject();
            current["child"] = child;
            current = child;
        }
        current["value"] = "complete";

        output.Write(root);

        using var document = System.Text.Json.JsonDocument.Parse(
            standardOutput.ToString(),
            new System.Text.Json.JsonDocumentOptions { MaxDepth = 256 });
        var value = document.RootElement;
        for (var index = 0; index < 80; index++)
        {
            value = value.GetProperty("child");
        }
        Assert.Equal("complete", value.GetProperty("value").GetString());
    }
}
