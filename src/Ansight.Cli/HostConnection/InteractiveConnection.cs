using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Ansight.Cli.HostConnection;

/// <summary>Owns one duplex pipe for the lifetime of an interactive CLI invocation.</summary>
internal sealed class InteractiveConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly string sessionId;
    private bool disposed;

    private InteractiveConnection(NamedPipeClientStream pipe, string sessionId)
    {
        this.pipe = pipe;
        this.sessionId = sessionId;
        reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
    }

    public static async Task<InteractiveConnection> OpenInteractiveConnectionAsync(
        CliArguments arguments, CancellationToken cancellationToken)
    {
        var sessionId = AppInteractionCommand.ValidateArguments(arguments);
        var metadata = CliRuntime.ReadMetadata(CliRuntime.ResolveOptions(arguments).DataDirectory);
        if (metadata is null || !CliRuntime.IsHostProcessRunning(metadata) || string.IsNullOrWhiteSpace(metadata.ControlPipeName))
            throw new CliHostUnavailableException("The interactive bridge requires the resident host. Start it with 'ansight host run' first.");

        var pipe = new NamedPipeClientStream(".", metadata.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        InteractiveConnection? connection = null;
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CliHostUnavailableException("The resident host control channel did not respond.");
            }

            connection = new InteractiveConnection(pipe, sessionId);
            await InteractionProtocol.WriteAsync(connection.writer,
                new ControlRequest(ControlProtocol.RequestSchema,
                    ControlClient.CreateForwardedArguments(arguments, Environment.CurrentDirectory), null), cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            else pipe.Dispose();
            throw;
        }
    }

    public Task<int> RunAsync(TextReader standardInput, CliOutput output, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ExchangeAsync(reader, writer, standardInput, output, sessionId, cancellationToken);
    }

    internal static async Task<int> ExchangeAsync(
        TextReader reader, TextWriter writer, TextReader standardInput, CliOutput output,
        string sessionId, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? sending = null;
        try
        {
            while (await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var schema = document.RootElement.GetProperty("schema").GetString();
                if (schema == ControlProtocol.ResponseSchema)
                {
                    var response = JsonSerializer.Deserialize<ControlResponse>(line, InteractionProtocol.jsonOptions)!;
                    if (response.ExitCode != 0 && (!string.IsNullOrWhiteSpace(response.StandardError) || !string.IsNullOrWhiteSpace(response.StandardOutput)))
                        output.WriteJsonLine(InteractionProtocol.Error(null, "closed", sessionId, "host_error",
                            response.StandardError + response.StandardOutput));
                    return response.ExitCode;
                }
                if (schema != InteractionProtocol.Schema)
                    throw new CliHostUnavailableException("The resident host returned an unsupported interactive response. Restart the host with the matching CLI version.");
                output.WriteJsonLine(document.RootElement);
                if (sending is null && document.RootElement.GetProperty("command").GetString() == "ready")
                    sending = SendAsync(standardInput, writer, lifetime);
            }
            throw new CliHostUnavailableException("The interactive connection closed; input may have completed. No actions were retried.");
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (sending is not null)
            {
                try { await sending.ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
    }

    private static async Task SendAsync(TextReader input, TextWriter writer, CancellationTokenSource lifetime)
    {
        try
        {
            var lines = new InteractionLineReader(input);
            while (await lines.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                await writer.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false);
                await writer.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
            await InteractionProtocol.WriteAsync(writer, new AppInteractionRequest("eof", "exit"), lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            reader.Dispose();
            pipe.Dispose();
        }
    }
}
