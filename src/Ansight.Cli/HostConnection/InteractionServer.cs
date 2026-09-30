using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;

namespace Ansight.Cli.HostConnection;

internal static class InteractionServer
{
    public static async Task<ControlResponse> RunAsync(
        CliArguments arguments,
        TextReader reader,
        TextWriter writer,
        Func<string, string?, IAppInteractionContext> createContext,
        ICliAccessAuthorizer accessAuthorizer,
        CancellationToken cancellationToken,
        CliAccessLease? existingLease = null)
    {
        var sessionId = AppInteractionCommand.ValidateArguments(arguments);
        var timeoutMs = arguments.HasFlag("timeout-ms") ? arguments.GetRequiredIntOption("timeout-ms", 100, 60000) : 15000;
        var taskTimeoutMs = arguments.HasFlag("task-timeout-ms") ? arguments.GetRequiredIntOption("task-timeout-ms", 100, 300000) : 120000;
        var repository = arguments.GetOption("repository");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Reading independently detects client death during an action. Only one queued request
        // is retained; this is not an unbounded action scheduler.
        var requests = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var receiving = ReceiveAsync(new InteractionLineReader(reader), requests.Writer, lifetime);
        var exitCode = CliExitCodes.Success;
        try
        {
            await using var ownedLease = existingLease is not { IsLocal: true }
                ? CliAccessLease.CreateLocal(lifetime.Token)
                : null;
            var lease = existingLease is { IsLocal: true } ? existingLease : ownedLease!;
            if (lease.IsDenied || !lease.Decision.IsAuthorized)
                throw new CliAccessDeniedException(lease.Failure);
            var context = createContext(sessionId, repository);
            using (var initialTimeout = CancellationTokenSource.CreateLinkedTokenSource(lease.Token))
            {
                initialTimeout.CancelAfter(timeoutMs);
                var initial = await context.ExecuteAsync(new("ready", "snapshot"), initialTimeout.Token).ConfigureAwait(false);
                await InteractionProtocol.WriteAsync(writer, initial with { Id = null, Command = "ready" }, lease.Token).ConfigureAwait(false);
            }

            await foreach (var line in requests.Reader.ReadAllAsync(lease.Token).ConfigureAwait(false))
            {
                AppInteractionRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<AppInteractionRequest>(line, InteractionProtocol.jsonOptions)
                        ?? throw new ArgumentException("Expected a JSON request object.");
                    request.Validate();
                    if (repository is null && (request.Command is "task" or "tasks"
                        || request.Commands?.Any(child => child.Command is "task" or "tasks") == true))
                        throw new ArgumentException("Task commands require --repository <root> when opening the interactive connection.");
                    if (request.Command == "exit")
                    {
                        await InteractionProtocol.WriteAsync(writer,
                            new AppInteractionResult(InteractionProtocol.Schema, request.Id, "exit", sessionId,
                                true, null, null, "Detached. The app and host remain running.", null, null, new(0, 0, 0)), lease.Token).ConfigureAwait(false);
                        break;
                    }

                    if (request.Command == "batch")
                    {
                        var batchTimer = Stopwatch.StartNew();
                        var results = new List<AppInteractionResult>();
                        foreach (var child in request.Commands!)
                        {
                            var childResult = await ExecuteAsync(context, child, timeoutMs, taskTimeoutMs, lease.Token).ConfigureAwait(false);
                            results.Add(childResult);
                            if (!childResult.Succeeded) break;
                        }
                        var skipped = request.Commands.Skip(results.Count).Select(child => child.Id).ToArray();
                        var succeeded = results.All(result => result.Succeeded);
                        await InteractionProtocol.WriteAsync(writer,
                            new InteractionBatchResult(InteractionProtocol.Schema, request.Id, "batch", sessionId,
                                succeeded, succeeded ? null : "batch_stopped",
                                succeeded ? "Batch completed in order." : "Batch stopped at the first failure. Completed actions were not rolled back or replayed.",
                                results, skipped, results.LastOrDefault()?.Screenshot,
                                new(results.Sum(result => result.Timing.InputMs),
                                    results.Sum(result => result.Timing.CaptureMs), batchTimer.Elapsed.TotalMilliseconds),
                                results.LastOrDefault()?.Ui), lease.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var result = await ExecuteAsync(context, request, timeoutMs, taskTimeoutMs, lease.Token).ConfigureAwait(false);
                        await InteractionProtocol.WriteAsync(writer, result, lease.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    await InteractionProtocol.WriteAsync(writer,
                        InteractionProtocol.Error(request?.Id, request?.Command ?? "invalid", sessionId, "invalid_request", exception.Message),
                        lease.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not IOException)
        {
            exitCode = exception is CliAccessDeniedException ? CliExitCodes.AccessDenied
                : exception is OperationCanceledException ? CliExitCodes.Cancelled : CliExitCodes.Failure;
            var message = exception is CliAccessDeniedException denied ? denied.Decision.Message
                : exception is OperationCanceledException ? "Interaction stopped; commands may have completed."
                : exception.Message;
            if (!lifetime.IsCancellationRequested)
                await InteractionProtocol.WriteAsync(writer,
                    InteractionProtocol.Error(null, "closed", sessionId, "session_closed", message), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await receiving.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        return new(ControlProtocol.ResponseSchema, exitCode, string.Empty, string.Empty);
    }

    private static async Task<AppInteractionResult> ExecuteAsync(
        IAppInteractionContext context, AppInteractionRequest request, int timeoutMs, int taskTimeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Command == "task" ? taskTimeoutMs : timeoutMs);
        try
        {
            return await context.ExecuteAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Never replay uncertain commands after a timeout, including task runs.
            return InteractionProtocol.Error(request.Id, request.Command, context.SessionId, "timeout",
                "The command or automatic capture timed out; it may have completed. Do not blindly retry.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return InteractionProtocol.Error(request.Id, request.Command, context.SessionId, "command_failed", exception.Message);
        }
    }

    private static async Task ReceiveAsync(
        InteractionLineReader reader,
        ChannelWriter<string> requests,
        CancellationTokenSource lifetime)
    {
        try
        {
            while (await reader.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                await requests.WriteAsync(line, lifetime.Token).ConfigureAwait(false);
            // EOF is a disconnected client. The CLI sends an explicit exit for ordinary stdin EOF.
            await lifetime.CancelAsync().ConfigureAwait(false);
            requests.TryComplete();
        }
        catch (Exception exception)
        {
            requests.TryComplete(exception);
            if (exception is IOException or OperationCanceledException)
                await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }
}
