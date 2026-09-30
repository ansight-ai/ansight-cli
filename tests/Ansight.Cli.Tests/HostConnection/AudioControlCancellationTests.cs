using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class AudioControlCancellationTests
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AudioCancellationKeepsPipeOpenUntilRemoteCleanupAndFinalResponse()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var cleanupAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = false;
        var remote = CompleteAfterCleanupAsync();
        var client = ControlClient.ReadCommandResponseAsync(
            CliArguments.Parse(["audio", "inject", "--session", "session-1", "--file", "quote.wav"]),
            pipe.ClientReader, pipe.ClientWriter,
            new CliOutput(true, TextWriter.Null, TextWriter.Null), cancelled.Token);

        cancelled.Cancel();
        await cancellationObserved.Task.WaitAsync(deadline.Token);
        Assert.False(client.IsCompleted);
        Assert.False(cleanedUp);
        Assert.True(pipe.Client.IsConnected);

        cleanupAllowed.SetResult();
        var response = await client.WaitAsync(deadline.Token);
        await remote.WaitAsync(deadline.Token);
        Assert.True(cleanedUp);
        Assert.NotNull(response);
        Assert.Equal(CliExitCodes.Cancelled, response.ExitCode);
        Assert.Contains("audio-cleanup-complete", response.StandardOutput);

        async Task CompleteAfterCleanupAsync()
        {
            await ControlServer.WaitForDisconnectAsync(pipe.ServerReader, acceptCancellation: true, deadline.Token);
            cancellationObserved.SetResult();
            await cleanupAllowed.Task.WaitAsync(deadline.Token);
            cleanedUp = true;
            var response = new ControlResponse(ControlProtocol.ResponseSchema, CliExitCodes.Cancelled,
                "audio-cleanup-complete", string.Empty);
            await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(response, jsonOptions).AsMemory(), deadline.Token);
            await pipe.ServerWriter.FlushAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task MissingCleanupAcknowledgementReportsUncertaintyWithinGrace()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var cancellationSignal = ControlServer.WaitForDisconnectAsync(pipe.ServerReader, true, deadline.Token);
        var client = ControlClient.ReadCommandResponseAsync(
            CliArguments.Parse(["audio", "inject"]), pipe.ClientReader, pipe.ClientWriter,
            new CliOutput(true, TextWriter.Null, TextWriter.Null), cancelled.Token,
            cleanupGrace: TimeSpan.FromMilliseconds(150));

        cancelled.Cancel();
        await cancellationSignal.WaitAsync(deadline.Token);
        var exception = await Assert.ThrowsAsync<CliHostUnavailableException>(
            () => client.WaitAsync(deadline.Token));
        Assert.Contains("did not confirm audio cancellation cleanup", exception.Message);
        Assert.Contains("Delivery may be partial", exception.Message);
        Assert.Contains("do not retry automatically", exception.Message);
    }

    [Fact]
    public async Task AbruptClientDisconnectStillSignalsServerCancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        var disconnected = ControlServer.WaitForDisconnectAsync(pipe.ServerReader, true, deadline.Token);
        pipe.Client.Dispose();
        await disconnected.WaitAsync(deadline.Token);
    }

    [Theory]
    [InlineData("audio", "capabilities")]
    [InlineData("ui", "tap")]
    public async Task OtherCommandsKeepImmediateCallerCancellation(string command, string action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        using var cancelled = new CancellationTokenSource();
        using var cancelWriter = new StringWriter();
        var client = ControlClient.ReadCommandResponseAsync(
            CliArguments.Parse([command, action]), pipe.ClientReader, cancelWriter,
            new CliOutput(true, TextWriter.Null, TextWriter.Null), cancelled.Token);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitAsync(deadline.Token));
        Assert.Empty(cancelWriter.ToString());
    }

    [Fact]
    public async Task ServerIgnoresCancelFramesForOtherCommands()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        var disconnected = ControlServer.WaitForDisconnectAsync(pipe.ServerReader, false, deadline.Token);
        await pipe.ClientWriter.WriteLineAsync(JsonSerializer.Serialize(new ControlCancel(ControlProtocol.CancelSchema), jsonOptions));
        await pipe.ClientWriter.FlushAsync(deadline.Token);
        Assert.False(disconnected.IsCompleted);
        pipe.Client.Dispose();
        await disconnected.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task ServerAcceptsOnlyRecognizedBoundedCancellationFrame()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = await ControlPipePair.ConnectAsync(deadline.Token);
        var cancellation = ControlServer.WaitForDisconnectAsync(pipe.ServerReader, true, deadline.Token);
        await pipe.ClientWriter.WriteLineAsync("not json");
        await pipe.ClientWriter.WriteLineAsync("{\"schema\":\"ansight.cli-control-cancel/v999\"}");
        await pipe.ClientWriter.WriteLineAsync(new string(' ', 1024) + JsonSerializer.Serialize(new ControlCancel(ControlProtocol.CancelSchema), jsonOptions));
        await pipe.ClientWriter.FlushAsync(deadline.Token);
        Assert.False(cancellation.IsCompleted);
        await pipe.ClientWriter.WriteLineAsync(JsonSerializer.Serialize(new ControlCancel(ControlProtocol.CancelSchema), jsonOptions));
        await pipe.ClientWriter.FlushAsync(deadline.Token);
        await cancellation.WaitAsync(deadline.Token);
    }

    private sealed class ControlPipePair : IDisposable
    {
        private ControlPipePair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
            ServerReader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            ClientReader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            ServerWriter = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true);
            ClientWriter = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true);
        }

        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }
        public StreamReader ServerReader { get; }
        public StreamReader ClientReader { get; }
        public StreamWriter ServerWriter { get; }
        public StreamWriter ClientWriter { get; }

        public static async Task<ControlPipePair> ConnectAsync(CancellationToken token)
        {
            // macOS named pipes use Unix sockets beneath its already long temp path.
            var name = $"aac-{Guid.NewGuid().ToString("N")[..16]}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var accepted = server.WaitForConnectionAsync(token);
                await client.ConnectAsync(token);
                await accepted;
                return new ControlPipePair(server, client);
            }
            catch
            {
                server.Dispose();
                client.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var resource in new IDisposable[] { ClientWriter, ServerWriter, ClientReader, ServerReader, Client, Server })
            {
                try { resource.Dispose(); }
                catch (Exception exception) when (exception is ObjectDisposedException or IOException) { }
            }
        }
    }
}
