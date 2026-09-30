using System.Text.Json;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class CliControlOutputTests
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OutputSinkFramesWritesAsControlMessages()
    {
        using var controlWriter = new StringWriter();
        var sink = new ControlOutputSink(controlWriter);
        using var standardError = sink.CreateTextWriter(ControlOutputStreams.StandardError);

        standardError.WriteLine("Starting workspace test.");

        var message = JsonSerializer.Deserialize<ControlOutput>(
            controlWriter.ToString().Trim(),
            jsonOptions);
        Assert.NotNull(message);
        Assert.Equal(ControlProtocol.OutputSchema, message.Schema);
        Assert.Equal(ControlOutputStreams.StandardError, message.Stream);
        Assert.Equal($"Starting workspace test.{Environment.NewLine}", message.Value);
    }

    [Fact]
    public async Task ClientWritesStreamFramesBeforeReturningFinalResponse()
    {
        var messages = new object[]
        {
            new ControlOutput(
                ControlProtocol.OutputSchema,
                ControlOutputStreams.StandardError,
                $"[1/2] Starting.{Environment.NewLine}"),
            new ControlOutput(
                ControlProtocol.OutputSchema,
                ControlOutputStreams.StandardOutput,
                $"Workspace tests complete.{Environment.NewLine}"),
            new ControlResponse(
                ControlProtocol.ResponseSchema,
                CliExitCodes.Success,
                string.Empty,
                string.Empty)
        };
        using var responseReader = new StringReader(string.Join(
            Environment.NewLine,
            messages.Select(message => JsonSerializer.Serialize(message, message.GetType(), jsonOptions))));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var output = new CliOutput(json: false, standardOutput, standardError);

        var response = await ControlClient.ReadResponseAsync(
            responseReader,
            output,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(CliExitCodes.Success, response.ExitCode);
        Assert.Equal($"Workspace tests complete.{Environment.NewLine}", standardOutput.ToString());
        Assert.Equal($"[1/2] Starting.{Environment.NewLine}", standardError.ToString());
    }
}
