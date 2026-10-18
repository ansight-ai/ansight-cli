using Ansight.Host.Activity;
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
    private RuntimeGeneration? generation;
    private readonly string dataDirectory;
    private readonly Action? stopHost;
    private readonly CliArguments? startupArguments;
    private readonly List<CancellationTokenSource> generationLifetimes = [];
    private Task? serverTask;

    public ControlServer(
        RuntimeCoordinator? runtime,
        string dataDirectory,
        CancellationToken shutdownToken,
        ICliAccessAuthorizer accessAuthorizer,
        CliAccessLease? accessLease = null,
        Action? stopHost = null,
        CliArguments? startupArguments = null)
    {
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        ArgumentNullException.ThrowIfNull(accessAuthorizer);
        if (runtime is not null) AttachGeneration(runtime, accessAuthorizer, accessLease, shutdownToken);
        this.stopHost = stopHost;
        this.startupArguments = startupArguments;
        PipeName = CreatePipeName(this.dataDirectory);
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    }

    public string PipeName { get; }

    public void AttachRuntime(RuntimeCoordinator runtime, ICliAccessAuthorizer authorizer,
        CliAccessLease lease, CancellationToken cancellationToken)
        => AttachGeneration(runtime, authorizer, lease, cancellationToken);

    private void AttachGeneration(RuntimeCoordinator runtime, ICliAccessAuthorizer authorizer,
        CliAccessLease? lease, CancellationToken cancellationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Keep cancelled token sources until server disposal so in-flight requests can link safely.
        generationLifetimes.Add(lifetime);
        Volatile.Write(ref generation, new(runtime, authorizer, lease, lifetime));
    }

    public void DetachRuntime() => Interlocked.Exchange(ref generation, null)?.Lifetime.Cancel();

    private sealed record RuntimeGeneration(RuntimeCoordinator Runtime, ICliAccessAuthorizer Authorizer,
        CliAccessLease? Lease, CancellationTokenSource Lifetime);

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
        DetachRuntime();
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
        foreach (var lifetime in generationLifetimes) lifetime.Dispose();
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
            finally
            {
                // The caller must receive the acknowledgement before cancellation closes its pipe.
                if (response.StopHostAfterReply) stopHost?.Invoke();
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
        if (request.Schema is not (ControlProtocol.RequestSchema or ControlProtocol.HostLeaseRequestSchema or ControlProtocol.RunnerAiRequestSchema)
            || request.Arguments is null
            || request.Arguments.Length == 0
            || request.Arguments.Length > MaximumArgumentCount
            || request.Arguments.Any(static argument => argument is null || argument.Length > 65_536))
        {
            throw new InvalidDataException("The CLI control request has an unsupported schema or argument shape.");
        }

        var parsedArguments = CliArguments.Parse(request.Arguments);
        if (parsedArguments.Positionals.Count >= 2 && parsedArguments.Positionals[0].Equals("host", StringComparison.OrdinalIgnoreCase)
            && parsedArguments.Positionals[1].ToLowerInvariant() is "run" or "stop" or "status")
        {
            if (request.Schema != ControlProtocol.RequestSchema || stopHost is null
                || request.ExpectedProcessId != Environment.ProcessId
                || request.ExpectedProcessStartedUtc is null
                || (new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime())
                    - request.ExpectedProcessStartedUtc.Value).Duration() >= TimeSpan.FromSeconds(2)
                || !string.Equals(Path.GetFullPath(CliRuntime.ResolveOptions(parsedArguments).DataDirectory),
                    dataDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The host identity or data directory changed. Inspect 'ansight host status' before retrying.");
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var output = new CliOutput(parsedArguments.IsJson, stdout, stderr, parsedArguments.IsSilent);
            if (parsedArguments.Positionals[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                parsedArguments.EnsurePositionalCount(2, "ansight host stop");
                return new ControlResponse(ControlProtocol.ResponseSchema, CliExitCodes.Success, "", "")
                    { StopHostAfterReply = true };
            }
            int code;
            try
            {
                code = parsedArguments.Positionals[1].Equals("status", StringComparison.OrdinalIgnoreCase)
                    ? HostCommands.ShowStatus(parsedArguments, output)
                    : HostCommands.ReuseHost(parsedArguments, startupArguments!,
                        Volatile.Read(ref generation)?.Runtime, output);
            }
            catch (CliUsageException exception)
            {
                code = CliExitCodes.Usage;
                output.WriteError("usage", exception.Message, code);
            }
            catch (CliHostUnavailableException exception)
            {
                code = CliExitCodes.HostUnavailable;
                output.WriteError("host_unavailable", exception.Message, code);
            }
            return new ControlResponse(ControlProtocol.ResponseSchema, code, stdout.ToString(), stderr.ToString());
        }

        using var callerContext = SessionActivityContext.Push(new SessionActivityContext(ActivityCaller.Declared(request.Caller)));
        var active = Volatile.Read(ref generation)
            ?? throw new CliHostUnavailableException("The host runtime is recovering. Retry shortly; 'ansight host stop' remains available.");
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Lifetime.Token);
        cancellationToken = requestLifetime.Token;
        try
        {
            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var arguments = CliArguments.Parse(request.Arguments);
            if (AppInteractionCommand.IsMatch(arguments))
            {
                return await InteractionServer.RunAsync(arguments, reader, responseWriter,
                    active.Runtime.CreateAppInteractionContext, active.Authorizer, cancellationToken, active.Lease, active.Runtime.Activity).ConfigureAwait(false);
            }
            var executionTask = ExecuteRequestAsync(request, responseWriter, active, commandCancellation.Token);
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
        catch (Exception exception) when (request.Schema is ControlProtocol.HostLeaseRequestSchema or ControlProtocol.RunnerAiRequestSchema)
        {
            var message = exception.GetBaseException().Message;
            if (!string.IsNullOrEmpty(request.RunnerAiCredentials?.ApiKey))
                message = message.Replace(request.RunnerAiCredentials.ApiKey, "[redacted]", StringComparison.Ordinal);
            return new ControlResponse(ResponseSchemaFor(request), CliExitCodes.Failure,
                string.Empty, $"Error: {message}{Environment.NewLine}");
        }
    }

    private async Task<ControlResponse> ExecuteRequestAsync(
        ControlRequest request,
        TextWriter responseWriter,
        RuntimeGeneration active,
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
                active,
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
            active,
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
        RuntimeGeneration active,
        CancellationToken cancellationToken)
    {
        var runtime = active.Runtime;
        var accessAuthorizer = active.Authorizer;
        var accessLease = active.Lease;
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
        if (request.Schema == ControlProtocol.RunnerAiRequestSchema
            && (string.IsNullOrWhiteSpace(request.RunnerAiCredentials?.ApiKey) || !CliCommandAccessPolicy.UsesRequestScopedRunnerCredentials(arguments)))
            throw new InvalidDataException("Runner credentials require a supported scoped command.");
        using var runnerContext = request.Schema == ControlProtocol.RunnerAiRequestSchema
            ? Ansight.Host.Workspaces.Execution.RunnerAiCredentialContext.Push(request.RunnerAiCredentials!) : null;
        using var verifiedCaller = request.Schema == ControlProtocol.HostLeaseRequestSchema
            ? SessionActivityContext.Push(SessionActivityContext.Current! with
            { Caller = SessionActivityContext.Current!.Caller with { PrincipalId = request.CallerUserId, PrincipalAttribution = "authenticated" } }) : null;
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
        => request.Schema == ControlProtocol.RunnerAiRequestSchema ? ControlProtocol.RunnerAiResponseSchema
            : request.Schema == ControlProtocol.HostLeaseRequestSchema
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
