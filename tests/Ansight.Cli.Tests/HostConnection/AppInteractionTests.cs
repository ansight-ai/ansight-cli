using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ansight.Cli.Auth;
using Ansight.Cli.Tests.TestSupport;
using Ansight.Cli.Commands.App;
using Ansight.Host.UiAutomation;

namespace Ansight.Cli.Tests.HostConnection;

public sealed class AppInteractionTests
{
    [Fact]
    public async Task OneConnectionHandlesReadyActionsMalformedRequestAndExit()
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var session = new RecordingContext();
        var creations = 0;
        var running = InteractionServer.RunAsync(Arguments(), input, output, (_, _) =>
        {
            creations++;
            return session;
        }, TestAccessAuthorizer.Allow, CancellationToken.None);
        Assert.Equal("ready", (await output.NextAsync()).GetProperty("command").GetString());
        for (var index = 0; index < 5; index++)
        {
            input.Send($$"""{"id":"{{index}}","command":"tap","x":0.5,"y":0.5}""");
            var result = await output.NextAsync();
            Assert.Equal(index.ToString(), result.GetProperty("id").GetString());
            Assert.True(result.GetProperty("succeeded").GetBoolean());
        }
        input.Send("{bad json}");
        Assert.Equal("invalid_request", (await output.NextAsync()).GetProperty("error").GetString());
        input.Send("""{"id":"bad","command":"tap","x":0.5,"y":0.5,"sessionId":"other"}""");
        Assert.Equal("invalid_request", (await output.NextAsync()).GetProperty("error").GetString());
        input.Send("""{"id":"bye","command":"exit"}""");
        Assert.Equal("exit", (await output.NextAsync()).GetProperty("command").GetString());
        Assert.Equal(0, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(1, creations);
        Assert.Equal(6, session.Requests.Count); // Initial automatic screenshot + five inputs.
    }

    [Fact]
    public async Task CloudDenialDoesNotPreventLocalInteraction()
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var session = new RecordingContext();
        var running = InteractionServer.RunAsync(Arguments(), input, output,
            (_, _) => session, TestAccessAuthorizer.Deny, CancellationToken.None);
        Assert.Equal("ready", (await output.NextAsync()).GetProperty("command").GetString());
        input.Send("""{"id":"bye","command":"exit"}""");
        Assert.Equal("exit", (await output.NextAsync()).GetProperty("command").GetString());
        Assert.Equal(CliExitCodes.Success, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Single(session.Requests);
    }

    [Fact]
    public async Task ResidentInteractionUsesAnAccountFreeHostLifetime()
    {
        var clock = new ManualTimeProvider();
        await using var lease = CliAccessLease.CreateLocal(CancellationToken.None, clock);
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var running = InteractionServer.RunAsync(Arguments(), input, output,
            (_, _) => new RecordingContext(), TestAccessAuthorizer.Deny, CancellationToken.None, lease);
        Assert.Equal("ready", (await output.NextAsync()).GetProperty("command").GetString());
        clock.Advance(TimeSpan.FromDays(365));
        input.Send("""{"id":"tap","command":"tap","x":0.5,"y":0.5}""");
        Assert.True((await output.NextAsync()).GetProperty("succeeded").GetBoolean());
        input.Send("""{"id":"bye","command":"exit"}""");
        await output.NextAsync();
        Assert.Equal(CliExitCodes.Success, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
    }

    [Fact]
    public async Task DisconnectCancelsAnInFlightAction()
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingContext
        {
            OnAction = async token =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            }
        };
        var running = InteractionServer.RunAsync(Arguments(), input, output,
            (_, _) => session, TestAccessAuthorizer.Allow, CancellationToken.None);
        await output.NextAsync();
        input.Send("""{"id":"1","command":"tap","x":0.5,"y":0.5}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.End();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CliExitCodes.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
    }

    [Fact]
    public async Task TimedOutActionIsNotReplayedAndTheConnectionAcceptsExit()
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var session = new RecordingContext { OnAction = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var running = InteractionServer.RunAsync(
            CliArguments.Parse(["app", "interact", "--session", "session-1", "--timeout-ms", "100"]), input, output,
            (_, _) => session, TestAccessAuthorizer.Allow, CancellationToken.None);
        await output.NextAsync();
        input.Send("""{"id":"1","command":"tap","x":0.5,"y":0.5}""");
        Assert.Equal("timeout", (await output.NextAsync()).GetProperty("error").GetString());
        input.Send("""{"id":"bye","command":"exit"}""");
        await output.NextAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, session.Requests.Count);
    }

    [Fact]
    public async Task ClientAndServerExchangeMultipleCommandsAndEofOverOneDuplexStream()
    {
        using var toServer = new InteractiveReader();
        using var toClient = new InteractiveReader();
        var clientOutput = new InteractiveWriter();
        using var commands = new StringReader("""
            {"id":"1","command":"tap","x":0.5,"y":0.5}
            {"id":"2","command":"swipe","x":0.5,"y":0.8,"endX":0.5,"endY":0.2}
            """);
        var session = new RecordingContext();
        var server = Task.Run(async () =>
        {
            var writer = new InteractiveWriter(toClient.Send);
            var result = await InteractionServer.RunAsync(Arguments(), toServer, writer,
                (_, _) => session, TestAccessAuthorizer.Allow, CancellationToken.None);
            await InteractionProtocol.WriteAsync(writer, result, CancellationToken.None);
        });
        var client = InteractiveConnection.ExchangeAsync(toClient, new InteractiveWriter(toServer.Send), commands,
            new CliOutput(false, clientOutput, TextWriter.Null), "session-1", CancellationToken.None);
        Assert.Equal(0, await client.WaitAsync(TimeSpan.FromSeconds(5)));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["ready", "tap", "swipe", "exit"], clientOutput.Lines.Select(line =>
            JsonDocument.Parse(line).RootElement.GetProperty("command").GetString()!).ToArray());
        Assert.Equal(3, session.Requests.Count);
    }

    [Theory]
    [InlineData(65537)]
    [InlineData(1000000)]
    public async Task OversizedUnterminatedInputIsBounded(int length)
    {
        var reader = new InteractionLineReader(new StringReader(new string('x', length)));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LineReaderHandlesCrLfAndFinalUnterminatedLine()
    {
        var reader = new InteractionLineReader(new StringReader("first\r\nsecond"));
        Assert.Equal("first", await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("second", await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CompletedLineIsDeliveredWhileFillBlockingInputRemainsOpen()
    {
        using var input = new FillBlockingReader();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reader = new InteractionLineReader(input);
        input.Send("{\"id\":\"catalog\",\"command\":\"tasks\"}\n");
        Assert.Equal("{\"id\":\"catalog\",\"command\":\"tasks\"}", await reader.ReadAsync(cancellation.Token));
        input.Send("{\"id\":\"next\",\"command\":\"snapshot\"}\r\n");
        Assert.Equal("{\"id\":\"next\",\"command\":\"snapshot\"}", await reader.ReadAsync(cancellation.Token));
    }

    /// <summary>Console.In may fill the requested character count across multiple terminal lines.</summary>
    private sealed class FillBlockingReader : TextReader
    {
        private readonly Channel<char> characters = Channel.CreateUnbounded<char>();
        public void Send(string value)
        {
            foreach (var character in value) characters.Writer.TryWrite(character);
        }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var count = 0;
            while (count < buffer.Length && await characters.Reader.WaitToReadAsync(cancellationToken))
                while (count < buffer.Length && characters.Reader.TryRead(out var character))
                    buffer.Span[count++] = character;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) characters.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void RequiresExplicitSessionAndRejectsCaptureDisablingOptions()
    {
        Assert.Throws<CliUsageException>(() => AppInteractionCommand.ValidateArguments(CliArguments.Parse(["app", "interact"])));
        Assert.Throws<CliUsageException>(() => AppInteractionCommand.ValidateArguments(CliArguments.Parse([
            "app", "interact", "--session", "session-1", "--no-capture"])));
        Assert.Equal("session-1", AppInteractionCommand.ValidateArguments(Arguments()));
        Assert.False(CliApplication.ShouldSuggestUpdate(Arguments()));
    }

    private static CliArguments Arguments() => CliArguments.Parse(["app", "interact", "--session", "session-1", "--jsonl"]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchRunsInOrderWithTaskSupportAndStopsOnFailure(bool fail)
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var context = new RecordingContext { FailingId = fail ? "run" : null };
        var repository = Path.GetFullPath(".");
        var running = InteractionServer.RunAsync(
            CliArguments.Parse(["app", "interact", "--session", "session-1", "--repository", repository]),
            input, output, (sessionId, root) =>
            {
                Assert.Equal("session-1", sessionId);
                Assert.Equal(repository, root);
                return context;
            }, TestAccessAuthorizer.Allow, CancellationToken.None);
        await output.NextAsync();
        input.Send("""{"id":"group","command":"batch","commands":[{"id":"tap","command":"tap","x":0.4,"y":0.6},{"id":"run","command":"task","taskId":"search.find","input":{"query":"test"}},{"id":"last","command":"snapshot"}]}""");
        var result = await output.NextAsync();
        Assert.Equal("group", result.GetProperty("id").GetString());
        Assert.Equal(!fail, result.GetProperty("succeeded").GetBoolean());
        Assert.Equal(fail ? 2 : 3, result.GetProperty("results").GetArrayLength());
        Assert.All(result.GetProperty("results").EnumerateArray(), item => Assert.True(item.TryGetProperty("screenshot", out _)));
        Assert.Equal(result.GetProperty("results")[fail ? 1 : 2].GetProperty("screenshot").GetRawText(),
            result.GetProperty("screenshot").GetRawText());
        Assert.Equal(result.GetProperty("results")[fail ? 1 : 2].GetProperty("ui").GetRawText(),
            result.GetProperty("ui").GetRawText());
        Assert.Equal((fail ? 2 : 3) * 2, result.GetProperty("timing").GetProperty("inputMs").GetDouble());
        Assert.Equal((fail ? 2 : 3) * 3, result.GetProperty("timing").GetProperty("captureMs").GetDouble());
        Assert.Equal(fail ? ["last"] : Array.Empty<string>(), result.GetProperty("skippedIds").EnumerateArray().Select(id => id.GetString()).ToArray());
        Assert.Equal(fail ? ["snapshot", "tap", "task"] : new[] { "snapshot", "tap", "task", "snapshot" },
            context.Requests.Select(request => request.Command).ToArray());
        Assert.Equal("test", context.Requests[2].Input!["query"]!.GetValue<string>());
        input.Send("""{"id":"bye","command":"exit"}""");
        await output.NextAsync();
        Assert.Equal(0, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
    }

    [Theory]
    [InlineData("{\"id\":\"bad\",\"command\":\"tap\"}")]
    [InlineData("{\"id\":\"first\",\"command\":\"back\"}")]
    [InlineData("{\"id\":\"bad\",\"command\":\"exit\"}")]
    [InlineData("{\"id\":\"bad\",\"command\":\"batch\",\"commands\":[]}")]
    [InlineData("{\"id\":\"bad\",\"command\":\"task\",\"taskId\":\"search.find\"}")]
    [InlineData("null")]
    [InlineData("{\"id\":\"bad\",\"command\":\"tap\",\"target\":{\"role\":\"button\"}}")]
    [InlineData("{\"id\":\"bad\",\"command\":\"tap\",\"x\":0.5,\"y\":0.5,\"target\":{\"text\":\"Search\"}}")]
    [InlineData("{\"id\":\"bad\",\"command\":\"type\",\"value\":\"query\",\"replaceExisting\":true}")]
    public async Task EntireBatchIsValidatedBeforeAnyChildRuns(string invalidChild)
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var context = new RecordingContext();
        var running = InteractionServer.RunAsync(Arguments(), input, output,
            (_, _) => context, TestAccessAuthorizer.Allow, CancellationToken.None);
        await output.NextAsync();
        input.Send($$"""{"id":"batch","command":"batch","commands":[{"id":"first","command":"back"},{{invalidChild}}]}""");
        Assert.Equal("invalid_request", (await output.NextAsync()).GetProperty("error").GetString());
        Assert.Single(context.Requests);
        input.Send("""{"id":"bye","command":"exit"}""");
        await output.NextAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BatchTimeoutSkipsRemainingCommandsWithoutReplay()
    {
        using var input = new InteractiveReader();
        var output = new InteractiveWriter();
        var context = new RecordingContext { OnAction = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var running = InteractionServer.RunAsync(
            CliArguments.Parse(["app", "interact", "--session", "session-1", "--timeout-ms", "100"]),
            input, output, (_, _) => context, TestAccessAuthorizer.Allow, CancellationToken.None);
        await output.NextAsync();
        input.Send("""{"id":"batch","command":"batch","commands":[{"id":"slow","command":"back"},{"id":"skip","command":"back"}]}""");
        var result = await output.NextAsync();
        Assert.Equal("timeout", result.GetProperty("results")[0].GetProperty("error").GetString());
        Assert.Equal("skip", result.GetProperty("skippedIds")[0].GetString());
        Assert.Equal(2, context.Requests.Count);
        input.Send("""{"id":"bye","command":"exit"}""");
        await output.NextAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("--repository", ".")]
    [InlineData("--repository=.", null)]
    public void RelativeRepositoryIsResolvedByClientBeforeOpeningConnection(string option, string? value)
    {
        var values = new List<string> { "app", "interact", "--session", "session-1", option };
        if (value is not null) values.Add(value);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "interactive-client");
        var forwarded = CliArguments.Parse(ControlClient.CreateForwardedArguments(CliArguments.Parse(values), workingDirectory));
        Assert.Equal(workingDirectory, forwarded.GetOption("repository"));
    }

    private sealed class RecordingContext : IAppInteractionContext
    {
        public string SessionId => "session-1";
        public List<AppInteractionRequest> Requests { get; } = [];
        public Func<CancellationToken, Task>? OnAction { get; init; }
        public string? FailingId { get; init; }

        public async Task<AppInteractionResult> ExecuteAsync(AppInteractionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Command != "snapshot" && OnAction is not null) await OnAction(cancellationToken);
            return new(InteractionProtocol.Schema, request.Id, request.Command, SessionId, request.Id != FailingId, null,
                request.Id == FailingId ? "task_failed" : null,
                "Completed.", null, new($"frame-{request.Id}", $"/tmp/frame-{request.Id}.png", DateTimeOffset.UtcNow, 390, 844),
                new(2, 3, 5), Ui: new("available", "test", request.Id, DateTimeOffset.UtcNow, [], false));
        }
    }

    private sealed class InteractiveReader : TextReader
    {
        private readonly Channel<char> characters = Channel.CreateUnbounded<char>();
        public void Send(string line)
        {
            foreach (var character in line + "\n") characters.Writer.TryWrite(character);
        }
        public void End() => characters.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!await characters.Reader.WaitToReadAsync(cancellationToken)) return 0;
            var count = 0;
            while (count < buffer.Length && characters.Reader.TryRead(out var character)) buffer.Span[count++] = character;
            return count;
        }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (await characters.Reader.WaitToReadAsync(cancellationToken))
            {
                while (characters.Reader.TryRead(out var character))
                {
                    if (character == '\n') return line.ToString();
                    line.Append(character);
                }
            }
            return line.Length == 0 ? null : line.ToString();
        }
    }

    private sealed class InteractiveWriter(Action<string>? onLine = null) : TextWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public List<string> Lines { get; } = [];
        public override void WriteLine(string? value)
        {
            Lines.Add(value!);
            lines.Writer.TryWrite(value!);
            onLine?.Invoke(value!);
        }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            WriteLine(buffer.ToString());
            return Task.CompletedTask;
        }
        public async Task<JsonElement> NextAsync()
        {
            var line = await lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
    }
}
