using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.HostConnection;

internal sealed class ControlServer : IAsyncDisposable
{
    private const int MaximumArgumentCount = 256;
    private const int MaximumRequestCharacters = 1_048_576;
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CancellationTokenSource shutdown;
    private readonly RuntimeCoordinator runtime;
    private readonly string dataDirectory;
    private readonly ICliAccessAuthorizer accessAuthorizer;
    private readonly CliAccessLease? accessLease;
    private Task? serverTask;

    public ControlServer(
        RuntimeCoordinator runtime,
        string dataDirectory,
        CancellationToken shutdownToken,
        ICliAccessAuthorizer accessAuthorizer,
        CliAccessLease? accessLease = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.accessAuthorizer = accessAuthorizer ?? throw new ArgumentNullException(nameof(accessAuthorizer));
        this.accessLease = accessLease;
        PipeName = CreatePipeName(this.dataDirectory);
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    }

    public string PipeName { get; }

    public void Start()
    {
        if (serverTask is not null)
        {
            throw new InvalidOperationException("The CLI control server has already started.");
        }

        serverTask = RunServerAsync(shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        if (serverTask is not null)
        {
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        shutdown.Dispose();
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionSafelyAsync(pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleConnectionSafelyAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: true);
            ControlResponse response;
            try
            {
                response = await HandleConnectionAsync(pipe, writer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                response = new ControlResponse(
                    ControlProtocol.ResponseSchema,
                    CliExitCodes.Failure,
                    string.Empty,
                    $"Error: {exception.GetBaseException().Message}{Environment.NewLine}");
            }

            try
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(response, jsonOptions).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
        }
    }

    private async Task<ControlResponse> HandleConnectionAsync(
        Stream pipe,
        TextWriter responseWriter,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (requestLine is null || requestLine.Length > MaximumRequestCharacters)
        {
            throw new InvalidDataException("The CLI control request is empty or exceeds 1 MB.");
        }

        var request = JsonSerializer.Deserialize<ControlRequest>(requestLine, jsonOptions)
                      ?? throw new InvalidDataException("The CLI control request is invalid.");
        if (request.Schema is not (ControlProtocol.RequestSchema or ControlProtocol.HostLeaseRequestSchema)
            || request.Arguments is null
            || request.Arguments.Length == 0
            || request.Arguments.Length > MaximumArgumentCount
            || request.Arguments.Any(static argument => argument is null || argument.Length > 65_536))
        {
            throw new InvalidDataException("The CLI control request has an unsupported schema or argument shape.");
        }

        try
        {
            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var arguments = CliArguments.Parse(request.Arguments);
            if (AppInteractionCommand.IsMatch(arguments))
            {
                return await InteractionServer.RunAsync(arguments, reader, responseWriter,
                    runtime.CreateAppInteractionContext, accessAuthorizer, cancellationToken, accessLease).ConfigureAwait(false);
            }
            var executionTask = ExecuteRequestAsync(request, responseWriter, commandCancellation.Token);
            var disconnectTask = WaitForDisconnectAsync(reader,
                ControlProtocol.SupportsCooperativeCancellation(arguments), commandCancellation.Token);
            if (await Task.WhenAny(executionTask, disconnectTask).ConfigureAwait(false) == disconnectTask)
                commandCancellation.Cancel();

            try
            {
                return await executionTask.ConfigureAwait(false);
            }
            finally
            {
                commandCancellation.Cancel();
                try
                {
                    await disconnectTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception exception) when (request.Schema == ControlProtocol.HostLeaseRequestSchema)
        {
            return new ControlResponse(ControlProtocol.HostLeaseResponseSchema, CliExitCodes.Failure,
                string.Empty, $"Error: {exception.GetBaseException().Message}{Environment.NewLine}");
        }
    }

    private async Task<ControlResponse> ExecuteRequestAsync(
        ControlRequest request,
        TextWriter responseWriter,
        CancellationToken cancellationToken)
    {
        if (request.StreamOutput)
        {
            var outputSink = new ControlOutputSink(responseWriter);
            using var streamedStandardOutput = outputSink.CreateTextWriter(ControlOutputStreams.StandardOutput);
            using var streamedStandardError = outputSink.CreateTextWriter(ControlOutputStreams.StandardError);
            var exitCode = await ExecuteRequestCoreAsync(
                request,
                streamedStandardOutput,
                streamedStandardError,
                cancellationToken).ConfigureAwait(false);
            return new ControlResponse(
                ResponseSchemaFor(request),
                exitCode,
                string.Empty,
                string.Empty);
        }

        using var standardOutput = new StringWriter(CultureInfo.InvariantCulture);
        using var standardError = new StringWriter(CultureInfo.InvariantCulture);
        var bufferedExitCode = await ExecuteRequestCoreAsync(
            request,
            standardOutput,
            standardError,
            cancellationToken).ConfigureAwait(false);
        return new ControlResponse(
            ResponseSchemaFor(request),
            bufferedExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private async Task<int> ExecuteRequestCoreAsync(
        ControlRequest request,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        var arguments = CliArguments.Parse(request.Arguments);
        var output = new CliOutput(
            arguments.IsJson,
            standardOutput,
            standardError,
            arguments.IsSilent);
        if (request.Schema == ControlProtocol.HostLeaseRequestSchema)
        {
            if (string.IsNullOrWhiteSpace(request.CallerUserId)
                || accessLease is null || accessLease.IsDenied || !accessLease.Decision.IsAuthorized
                || accessLease.Decision.UserId != request.CallerUserId)
                return AccessDecision.AuthenticationRequired.WriteFailure(output);
            if (accessAuthorizer is not ICliLocalAccessProbe localProbe)
                return AccessDecision.Unavailable.WriteFailure(output);
            var localFailure = await localProbe.CheckLocalAsync(request.CallerUserId, cancellationToken)
                .ConfigureAwait(false);
            if (localFailure is not null) return localFailure.WriteFailure(output);
            if (accessLease.IsDenied) return accessLease.Failure.WriteFailure(output);
        }
        using var context = CliCommandContext.Push(
            runtime,
            dataDirectory,
            request.SecretValue,
            request.SecretValues);
        return await CliApplication.RunParsedAsync(
            arguments,
            output,
            cancellationToken,
            allowResidentHostForwarding: false,
            accessAuthorizer: accessAuthorizer,
            existingLease: accessLease).ConfigureAwait(false);
    }

    private static string ResponseSchemaFor(ControlRequest request)
        => request.Schema == ControlProtocol.HostLeaseRequestSchema
            ? ControlProtocol.HostLeaseResponseSchema : ControlProtocol.ResponseSchema;

    internal static async Task WaitForDisconnectAsync(
        TextReader reader,
        bool acceptCancellation,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        var oversizedLine = false;
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            if (!acceptCancellation) continue;
            if (buffer[0] == '\n')
            {
                if (!oversizedLine && IsCancellationRequest(line.ToString())) return;
                line.Clear();
                oversizedLine = false;
            }
            else if (line.Length < 256)
            {
                line.Append(buffer[0]);
            }
            else
            {
                oversizedLine = true;
            }
        }
    }

    private static bool IsCancellationRequest(string line)
    {
        try
        {
            using var message = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            return message.RootElement.ValueKind == JsonValueKind.Object
                   && message.RootElement.TryGetProperty("schema", out var schema)
                   && schema.ValueKind == JsonValueKind.String
                   && schema.GetString() == ControlProtocol.CancelSchema;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreatePipeName(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory));
        return $"ansight-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
