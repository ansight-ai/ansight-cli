namespace Ansight.Cli.Commands.App;

internal static class AppInteractionCommand
{
    public static bool IsMatch(CliArguments arguments)
        => arguments.Positionals.Count >= 2
           && arguments.Positionals[0].Equals("app", StringComparison.OrdinalIgnoreCase)
           && arguments.Positionals[1].Equals("interact", StringComparison.OrdinalIgnoreCase);

    internal static string ValidateArguments(CliArguments arguments)
    {
        arguments.EnsurePositionalCount(2, "ansight app interact --session <id> [--repository <root>] [--jsonl]");
        arguments.EnsureOptionContract("ansight app interact", ["jsonl", "json", "verbose", "diagnostic"],
            ["session", "repository", "timeout-ms", "task-timeout-ms", "data-dir"]);
        var sessionId = arguments.GetOption("session");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new CliUsageException("app interact requires --session <id>. Use 'ansight session list --connected --json' to choose one.");
        if (arguments.HasFlag("timeout-ms")) arguments.GetRequiredIntOption("timeout-ms", 100, 60000);
        if (arguments.HasFlag("task-timeout-ms")) arguments.GetRequiredIntOption("task-timeout-ms", 100, 300000);
        if (arguments.HasFlag("repository") && string.IsNullOrWhiteSpace(arguments.GetOption("repository")))
            throw new CliUsageException("--repository requires a repository root path.");
        return sessionId;
    }

    public static async Task<int> RunAsync(
        CliArguments arguments, CliOutput output, TextReader standardInput, CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
            return CliCommandHelp.Write(output, Help);
        await using var connection = await InteractiveConnection.OpenInteractiveConnectionAsync(
            arguments, cancellationToken).ConfigureAwait(false);
        return await connection.RunAsync(standardInput, output, cancellationToken).ConfigureAwait(false);
    }

    private const string Help = """
        Interact with a connected app through one persistent JSON Lines process and pipe.

        Usage: ansight app interact --session <id> [--repository <root>] [--jsonl]
               [--timeout-ms <100-60000>] [--task-timeout-ms <100-300000>]

        Ready and action responses include compact fresh ui.nodes and persisted screenshots.
        Prefer semantic targets; each is resolved freshly and must match one visible enabled
        node. Use screenshots and normalized coordinates (0-1) for missing tree semantics.
        Targeted type focuses and replaces by default (replaceExisting:false appends).
        Untargeted type appends to the focused field. Never mix target and coordinates.
        Send one JSON object per line; correlate results by id. Evidence is automatic.

          {"id":"search","command":"type","target":{"automationId":"search-field"},"value":"Kalymnos"}
          {"id":"select","command":"tap","target":{"text":"Kalymnos","role":"button"}}
          {"id":"1","command":"tap","x":0.5,"y":0.8}
          {"id":"2","command":"swipe","x":0.5,"y":0.8,"endX":0.5,"endY":0.2}
          {"id":"3","command":"pinch","x":0.5,"y":0.5,"scale":1.5}
          {"id":"4","command":"type","value":"Kalymnos"}
          {"id":"5","command":"back"}
          {"id":"6","command":"snapshot"}
          {"id":"7","command":"tasks"}
          {"id":"8","command":"task","taskId":"search.find","input":{"query":"Kalymnos"}}
          {"id":"9","command":"batch","commands":[{"id":"9a","command":"tap","x":0.5,"y":0.8},{"id":"9b","command":"type","value":"Kalymnos"}]}
          {"id":"10","command":"exit"}

        tasks/task use the repository pinned at connection startup and the existing task
        engine, with the same app/session. Task results include status and assertions;
        only Passed is successful. Default action timeout: 15s; task timeout: 120s.
        Batches contain 1-32 commands (including tasks), validated before any execute.
        They run sequentially, capture every action, and stop at the first failure.
        The batch response contains ordered results, skippedIds, final-child ui/screenshot,
        and aggregate timing. No rollback/replay,
        nested batches or exit within a batch. Every id in a batch must be unique.
        Requests are limited to 65536 characters per JSON line.

        Batch only known sequences; read ui before choosing unknown targets. View images
        for visual questions or insufficient semantics, not routinely after every child.
        Do not pre-type input that a maintained task will enter itself. snapshot is optional for
        observing asynchronous changes; action evidence is always automatic. Responses
        include screenshot paths, frame IDs, previous-frame references, and timing.
        Automatic screenshot settling uses 300ms quietness, a 900ms unchanged grace,
        and a 2s sampling window. screenshot.settling reports stable/unchanged/timed_out;
        quietness is not semantic completion. ui_unsettled stops a batch without replay.
        Malformed requests do not close the process. Failed/uncertain actions are never
        automatically replayed. Exit or stdin EOF detaches, leaving the app/host running.
        Ctrl+C cancels the connection. No nested agent or app execute is involved.
        """;
}
