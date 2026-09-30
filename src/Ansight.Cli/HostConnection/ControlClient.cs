using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.HostConnection;

internal static class ControlClient
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int?> TryRunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
        => await TryRunCoreAsync(arguments, output, cancellationToken, null).ConfigureAwait(false);

    internal static Task<int?> TryRunWithHostLeaseAsync(
        CliArguments arguments,
        CliOutput output,
        string callerUserId,
        CancellationToken cancellationToken)
        => TryRunCoreAsync(arguments, output, cancellationToken, callerUserId);

    private static async Task<int?> TryRunCoreAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken,
        string? callerUserId)
    {
        if (!ShouldForward(arguments))
        {
            return null;
        }

        var options = CliRuntime.ResolveOptions(arguments);
        var metadata = CliRuntime.ReadMetadata(options.DataDirectory);
        if (metadata is null || !CliRuntime.IsHostProcessRunning(metadata))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(metadata.ControlPipeName))
        {
            throw new CliHostUnavailableException(
                "The running Ansight host does not expose a CLI control channel. Restart it with this CLI version.");
        }

        var forwardedArguments = CreateForwardedArguments(arguments, Environment.CurrentDirectory);
        var secretValue = await ReadForwardedSecretAsync(arguments, cancellationToken).ConfigureAwait(false);
        var secretValues = CollectForwardedSecretValues(
            CliArguments.Parse(forwardedArguments),
            CliCommandContext.ResolveScopedSecret,
            cancellationToken);
        var request = new ControlRequest(
            callerUserId is null ? ControlProtocol.RequestSchema : ControlProtocol.HostLeaseRequestSchema,
            forwardedArguments,
            secretValue,
            StreamOutput: true,
            SecretValues: secretValues,
            CallerUserId: callerUserId);

        using var pipe = new NamedPipeClientStream(
            ".",
            metadata.ControlPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliHostUnavailableException(
                $"The resident host control channel '{metadata.ControlPipeName}' did not respond.");
        }
        catch (IOException exception)
        {
            throw new CliHostUnavailableException(
                $"The resident host control channel is unavailable: {exception.Message}");
        }

        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(request, jsonOptions).AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = await ReadCommandResponseAsync(arguments, reader, writer, output, cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            throw new CliHostUnavailableException(
                "The resident host disconnected before returning the command result. The operation may have run partially; inspect the latest trace before retrying.");
        }
        if (callerUserId is not null
            && string.Equals(response.Schema, ControlProtocol.ResponseSchema, StringComparison.Ordinal))
        {
            // An older host rejected v2 before executing anything. Use the normal access path.
            return null;
        }
        var expectedSchema = callerUserId is null
            ? ControlProtocol.ResponseSchema : ControlProtocol.HostLeaseResponseSchema;
        if (!string.Equals(response.Schema, expectedSchema, StringComparison.Ordinal))
        {
            throw new CliHostUnavailableException("The resident host returned an invalid CLI control response.");
        }

        output.WriteRaw(response.StandardOutput, response.StandardError);
        return response.ExitCode;
    }

    internal static async Task<bool?> TryHasConnectedAppSessionAsync(
        CliArguments arguments,
        string requestedTarget,
        string? requestedSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedTarget);
        var probeArguments = CliArguments.Parse(
        [
            "session",
            "list",
            "--connected",
            "--json",
            "--data-dir",
            CliRuntime.ResolveOptions(arguments).DataDirectory
        ]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var output = new CliOutput(true, standardOutput, standardError);
        int? exitCode;
        try
        {
            exitCode = await TryRunAsync(probeArguments, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliHostUnavailableException)
        {
            return false;
        }

        if (exitCode is null)
        {
            return false;
        }
        if (exitCode != CliExitCodes.Success)
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<SessionListOutput>(
                standardOutput.ToString(),
                jsonOptions);
            if (result is null)
            {
                return null;
            }

            var normalizedTarget = requestedTarget.Trim();
            var normalizedSessionId = string.IsNullOrWhiteSpace(requestedSessionId)
                ? null
                : requestedSessionId.Trim();
            return result.Sessions.Any(session => normalizedSessionId is null
                ? session.AppId.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                  || session.SessionId.Equals(normalizedTarget, StringComparison.Ordinal)
                : session.SessionId.Equals(normalizedSessionId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Task<ControlResponse?> ReadCommandResponseAsync(
        CliArguments arguments,
        TextReader reader,
        TextWriter writer,
        CliOutput output,
        CancellationToken cancellationToken,
        TimeSpan? cleanupGrace = null)
        => ControlProtocol.SupportsCooperativeCancellation(arguments)
            ? ReadAudioResponseAsync(reader, writer, output, cancellationToken,
                cleanupGrace ?? TimeSpan.FromSeconds(10))
            : ReadResponseAsync(reader, output, cancellationToken);

    private static async Task<ControlResponse?> ReadAudioResponseAsync(
        TextReader reader,
        TextWriter writer,
        CliOutput output,
        CancellationToken cancellationToken,
        TimeSpan cleanupGrace)
    {
        using var responseCancellation = new CancellationTokenSource();
        var responseTask = ReadResponseAsync(reader, output, responseCancellation.Token);
        var cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelRequested);
        var cancellationSent = false;
        try
        {
            if (await Task.WhenAny(responseTask, cancelRequested.Task).ConfigureAwait(false) == responseTask)
                return await responseTask.ConfigureAwait(false);

            // Keep the response read alive while the host stops the provider and persists
            // its final evidence. Closing this pipe would cancel without acknowledging cleanup.
            cancellationSent = true;
            responseCancellation.CancelAfter(cleanupGrace);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(new ControlCancel(ControlProtocol.CancelSchema), jsonOptions).AsMemory(),
                responseCancellation.Token).ConfigureAwait(false);
            await writer.FlushAsync(responseCancellation.Token).ConfigureAwait(false);
            return await responseTask.ConfigureAwait(false)
                ?? throw new CliHostUnavailableException(
                    "The host disconnected before confirming audio cancellation cleanup. Delivery may be partial; do not retry automatically.");
        }
        catch (OperationCanceledException) when (responseCancellation.IsCancellationRequested)
        {
            throw new CliHostUnavailableException(
                $"The host did not confirm audio cancellation cleanup within {cleanupGrace.TotalSeconds:0.###} seconds. Delivery may be partial; do not retry automatically.");
        }
        catch (IOException exception) when (cancellationSent)
        {
            throw new CliHostUnavailableException(
                $"The control pipe closed before audio cancellation cleanup was confirmed. Delivery may be partial; do not retry automatically. {exception.Message}");
        }
        finally
        {
            responseCancellation.Cancel();
            // Observe a pending read that faults after the pipe is disposed on a write failure.
            _ = responseTask.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal static async Task<ControlResponse?> ReadResponseAsync(
        TextReader reader,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        while (true)
        {
            var responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (responseLine is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(responseLine);
            var schema = document.RootElement.TryGetProperty("schema", out var schemaElement)
                ? schemaElement.GetString()
                : null;
            if (schema is ControlProtocol.ResponseSchema or ControlProtocol.HostLeaseResponseSchema)
            {
                return JsonSerializer.Deserialize<ControlResponse>(responseLine, jsonOptions);
            }

            if (!string.Equals(schema, ControlProtocol.OutputSchema, StringComparison.Ordinal))
            {
                throw new CliHostUnavailableException(
                    "The resident host returned an unsupported CLI control message.");
            }

            var streamedOutput = JsonSerializer.Deserialize<ControlOutput>(responseLine, jsonOptions)
                                 ?? throw new CliHostUnavailableException(
                                     "The resident host returned invalid streamed CLI output.");
            switch (streamedOutput.Stream)
            {
                case ControlOutputStreams.StandardOutput:
                    output.WriteRaw(streamedOutput.Value, string.Empty);
                    break;
                case ControlOutputStreams.StandardError:
                    output.WriteRaw(string.Empty, streamedOutput.Value);
                    break;
                default:
                    throw new CliHostUnavailableException(
                        $"The resident host returned unknown CLI output stream '{streamedOutput.Stream}'.");
            }
        }
    }

    internal static string[] CreateForwardedArguments(
        CliArguments arguments,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var forwardedArguments = arguments.OriginalArguments.ToArray();
        if (arguments.Positionals.Count >= 3
            && arguments.Positionals[0].ToLowerInvariant() is "workspace" or "workspaces"
            && arguments.Positionals[1].ToLowerInvariant() is "init" or "initialize")
        {
            var workspaceWorkingDirectory = Path.GetFullPath(workingDirectory);
            var workspacePathArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(2);
            forwardedArguments[workspacePathArgumentIndex] = ResolvePath(
                forwardedArguments[workspacePathArgumentIndex],
                workspaceWorkingDirectory);
            ResolvePathOptions(forwardedArguments, workspaceWorkingDirectory);
            return forwardedArguments;
        }

        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (arguments.Positionals.Count >= 3
            && arguments.Positionals[0].Equals("replay", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[1].ToLowerInvariant() is "sentry" or "posthog" or "post-hog")
        {
            var sourceArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(2);
            var source = forwardedArguments[sourceArgumentIndex];
            var candidatePath = Path.GetFullPath(source, fullWorkingDirectory);
            if (File.Exists(candidatePath)
                || Path.IsPathFullyQualified(source)
                || Path.GetExtension(source).ToLowerInvariant() is ".json" or ".jsonl" or ".ndjson")
            {
                forwardedArguments[sourceArgumentIndex] = candidatePath;
            }
        }

        if (arguments.Positionals.Count >= 6
            && arguments.Positionals[0].Equals("device", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[1].Equals("location", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[2].ToLowerInvariant() is "play" or "replay")
        {
            var routeArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(5);
            forwardedArguments[routeArgumentIndex] = ResolvePath(
                forwardedArguments[routeArgumentIndex],
                fullWorkingDirectory);
        }

        if (arguments.Positionals.Count >= 5
            && arguments.Positionals[0].Equals("cloud", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[1].ToLowerInvariant() is "attachment" or "attachments"
            && arguments.Positionals[2].Equals("upload", StringComparison.OrdinalIgnoreCase))
        {
            var attachmentArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(4);
            forwardedArguments[attachmentArgumentIndex] = ResolvePath(
                forwardedArguments[attachmentArgumentIndex],
                fullWorkingDirectory);
        }

        if (arguments.Positionals.Count >= 4
            && arguments.Positionals[0].Equals("cloud", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[1].ToLowerInvariant() is "test" or "tests"
            && arguments.Positionals[2].Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            var resultsArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(3);
            forwardedArguments[resultsArgumentIndex] = ResolvePath(
                forwardedArguments[resultsArgumentIndex],
                fullWorkingDirectory);
        }

        ResolvePathOptions(
            forwardedArguments,
            fullWorkingDirectory,
            resolveAppOption: arguments.Positionals[0].ToLowerInvariant() is not ("ui" or "keyboard"),
            resolvePolicyOption: arguments.Positionals.Count >= 2
                                 && arguments.Positionals[0].ToLowerInvariant() is ("session" or "sessions")
                                 && arguments.Positionals[1].ToLowerInvariant() is ("share" or "share-batch"));

        if (arguments.Positionals.Count >= 4
            && arguments.Positionals[0].ToLowerInvariant() is "test" or "tests"
            && arguments.Positionals[1].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            var outputArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(3);
            forwardedArguments[outputArgumentIndex] = ResolvePath(
                forwardedArguments[outputArgumentIndex],
                fullWorkingDirectory);
            return forwardedArguments;
        }

        if (arguments.Positionals.Count < 3
            || arguments.Positionals[0].ToLowerInvariant() is not ("test" or "tests")
            || arguments.Positionals[1].ToLowerInvariant() is not ("run" or "run-all"))
        {
            return forwardedArguments;
        }

        var workspaceArgumentIndex = arguments.GetOriginalArgumentIndexForPositional(2);
        forwardedArguments[workspaceArgumentIndex] = ResolvePath(
            forwardedArguments[workspaceArgumentIndex],
            fullWorkingDirectory);
        ResolvePathOptions(forwardedArguments, fullWorkingDirectory);
        return forwardedArguments;
    }

    private static void ResolvePathOptions(
        string[] arguments,
        string workingDirectory,
        bool resolveAppOption = true,
        bool resolvePolicyOption = false)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = argument.IndexOf('=', StringComparison.Ordinal);
            var optionName = (separatorIndex >= 0 ? argument[2..separatorIndex] : argument[2..])
                .Trim();
            if (!resolveAppOption
                && optionName.Equals("app", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resolvePolicyOption
                && optionName.Equals("policy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (optionName.ToLowerInvariant() is not (
                    "app" or "ipa" or "application-path" or "result-file" or "data-dir"
                    or "workspace" or "repository" or "output" or "file" or "instructions-file"
                    or "prompt-file" or "sanitizer" or "policy" or "input-file"))
            {
                continue;
            }

            if (separatorIndex >= 0)
            {
                var value = argument[(separatorIndex + 1)..];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    arguments[index] = argument[..(separatorIndex + 1)] + ResolvePath(value, workingDirectory);
                }

                continue;
            }

            if (index + 1 < arguments.Length
                && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                index++;
                arguments[index] = ResolvePath(arguments[index], workingDirectory);
            }
        }
    }

    private static string ResolvePath(string path, string workingDirectory)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()), workingDirectory);

    internal static bool ShouldForward(CliArguments arguments)
    {
        // This command owns a duplex connection rather than a one-command forward.
        if (AppInteractionCommand.IsMatch(arguments)) return false;
        if (arguments.Positionals.Count == 0)
        {
            return false;
        }

        var command = arguments.Positionals[0].ToLowerInvariant();
        if (command is "serve"
            or "ui"
            or "keyboard"
            or "audio"
            or "secret" or "secrets"
            or "session" or "sessions"
            or "artifact" or "artifacts"
            or "cloud"
            or "app"
            or "app-graph" or "app-graphs"
            or "replay"
            or "task" or "tasks"
            or "pairing" or "enrollment"
            or "companion" or "remote" or "remote-simulator"
            or "profile" or "profiling"
            or "repo" or "repository")
        {
            return true;
        }

        if (command == "account")
        {
            return arguments.Positionals.Count > 1
                   && arguments.Positionals[1].ToLowerInvariant() is "grant" or "grants";
        }

        if (command is "device" or "devices")
        {
            return arguments.Positionals.Count >= 3
                   && arguments.Positionals[1].Equals("location", StringComparison.OrdinalIgnoreCase)
                   && arguments.Positionals[2].ToLowerInvariant() is "play" or "replay" or "status" or "stop";
        }

        if (command is "workspace" or "workspaces")
        {
            return arguments.Positionals.Count > 1
                   && arguments.Positionals[1].ToLowerInvariant() is "init" or "initialize";
        }

        if (command == "trends")
        {
            return arguments.Positionals.Count > 1
                   && arguments.Positionals[1].ToLowerInvariant() is "history" or "rebuild";
        }

        return command is "test" or "tests"
               && arguments.Positionals.Count > 1
               && arguments.Positionals[1].ToLowerInvariant() is "run" or "run-inline" or "run-all"
                   or "history" or "inspect" or "export";
    }

    internal static IReadOnlyDictionary<string, string> CollectForwardedSecretValues(
        CliArguments arguments,
        Func<string, string?> environmentVariableResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environmentVariableResolver);
        var aliases = ResolveForwardedSecretAliases(arguments, cancellationToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            var value = environmentVariableResolver(alias);
            if (!string.IsNullOrEmpty(value))
            {
                values[alias] = value;
            }
        }
        return values;
    }

    private static IReadOnlyList<string> ResolveForwardedSecretAliases(
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Positionals.Count < 2)
        {
            return [];
        }

        var command = arguments.Positionals[0].ToLowerInvariant();
        var subcommand = arguments.Positionals[1].ToLowerInvariant();
        if ((command is "test" or "tests" && subcommand == "run-inline")
            || (command == "app" && subcommand == "execute")
            || ((command is "task" or "tasks") && subcommand == "run"))
        {
            return arguments.GetOptions("secret")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (command is not ("test" or "tests")
            || subcommand is not ("run" or "run-all")
            || arguments.Positionals.Count < 3)
        {
            return [];
        }

        var catalog = WorkspaceTestCatalog.Load(arguments.Positionals[2], cancellationToken);
        IEnumerable<WorkspaceTestDefinition> tests = catalog.Tests;
        if (subcommand == "run")
        {
            if (arguments.Positionals.Count < 4)
            {
                return [];
            }
            var testId = arguments.Positionals[3];
            tests = tests.Where(test => string.Equals(
                test.TestId,
                testId,
                StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var requestedTestIds = arguments.GetOptions("test");
            if (requestedTestIds.Count > 0)
            {
                var requestedIds = requestedTestIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                tests = tests.Where(test => requestedIds.Contains(test.TestId));
            }
        }

        return tests
            .SelectMany(static test => test.RequiredSecrets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Task<string?> ReadForwardedSecretAsync(
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Positionals.Count >= 2
            && arguments.Positionals[0].ToLowerInvariant() is "secret" or "secrets"
            && arguments.Positionals[1].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            arguments.EnsurePositionalCount(
                4,
                "ansight secret set <app-id> <alias> [--stdin|--from-env <NAME>]");
            return ReadSecretAsync(arguments, "Secret value: ", cancellationToken);
        }

        if (arguments.Positionals.Count >= 3
            && arguments.Positionals[0].Equals("replay", StringComparison.OrdinalIgnoreCase)
            && arguments.Positionals[1].ToLowerInvariant() is "sentry" or "posthog" or "post-hog"
            && !File.Exists(arguments.Positionals[2]))
        {
            var tokenEnvironmentVariable = arguments.GetOption("token-env")
                                           ?? (arguments.Positionals[1].Equals(
                                                   "sentry",
                                                   StringComparison.OrdinalIgnoreCase)
                                               ? "SENTRY_AUTH_TOKEN"
                                               : "POSTHOG_PERSONAL_API_KEY");
            return Task.FromResult(Environment.GetEnvironmentVariable(tokenEnvironmentVariable));
        }

        return Task.FromResult<string?>(null);
    }

    private static async Task<string?> ReadSecretAsync(
        CliArguments arguments,
        string prompt,
        CancellationToken cancellationToken)
        => await SecretInput.ReadAsync(arguments, prompt, cancellationToken).ConfigureAwait(false);
}
