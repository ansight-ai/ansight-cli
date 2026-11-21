using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ansight.RemoteSimulator.Core.Agent;

namespace Ansight.Cli.LocalSimulator;

internal interface IClaudeAnnotationClient
{
    Task<IReadOnlyList<RemoteAgentChatSession>> ListAsync(string root, CancellationToken cancellationToken);
    Task SendAsync(string root, string threadId, bool newChat, CompanionAnnotationEvidence evidence,
        Action<string, string> update, CancellationToken cancellationToken);
}

internal sealed class ClaudeAnnotationClient(string? executable = null) : IClaudeAnnotationClient
{
    internal static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ANSIGHT_CLAUDE_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, name)).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Install Claude Code and sign in with claude auth login on this machine.");
    }

    public Task<IReadOnlyList<RemoteAgentChatSession>> ListAsync(string root, CancellationToken cancellationToken)
    {
        _ = ResolveExecutable();
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Task.FromResult(ReadChats(Path.Combine(home, "projects", Regex.Replace(root, "[^a-zA-Z0-9]", "-")), root, cancellationToken));
    }

    internal static IReadOnlyList<RemoteAgentChatSession> ReadChats(string directory, string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var chats = new List<RemoteAgentChatSession>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out _))
            {
                continue;
            }

            try
            {
                string? title = null, preview = null;
                var belongs = false;
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var entry = document.RootElement;
                        if (entry.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True)
                        {
                            continue;
                        }

                        if (entry.TryGetProperty("cwd", out var cwd) && cwd.GetString() == root)
                        {
                            belongs = true;
                        }

                        if (entry.TryGetProperty("customTitle", out var name) && name.ValueKind == JsonValueKind.String)
                        {
                            title = name.GetString();
                        }

                        if (preview is null && entry.TryGetProperty("type", out var type) && type.GetString() == "user"
                            && entry.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                        {
                            preview = content.ValueKind == JsonValueKind.String ? content.GetString()
                                : content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().Where(c => c.TryGetProperty("text", out _)).Select(c => c.GetProperty("text").GetString()).FirstOrDefault() : null;
                        }
                    }
                    catch (JsonException) { /* A live transcript may end with a partial line. */ }
                }
                if (!belongs)
                {
                    continue;
                }

                var label = (title ?? preview ?? "Claude Code chat").Split('\n')[0];
                chats.Add(new("claude", Path.GetFileNameWithoutExtension(file.Name), label[..Math.Min(160, label.Length)], root,
                    new DateTimeOffset(file.LastWriteTimeUtc), "idle"));
            }
            catch (IOException) { /* A concurrently removed transcript is no longer selectable. */ }
        }
        return chats;
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string root, string threadId, bool newChat)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages",
            newChat ? "--session-id" : "--resume", threadId })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    public async Task SendAsync(string root, string threadId, bool newChat, CompanionAnnotationEvidence evidence,
        Action<string, string> update, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable ?? ResolveExecutable(), root, threadId, newChat) };
        if (!process.Start())
        {
            throw new IOException("Claude Code could not start.");
        }

        var errorTask = DrainErrorsAsync(process.StandardError, cancellationToken);
        var sawResult = false;
        try
        {
            var content = new List<object> { new { type = "text", text = evidence.Prompt } };
            if (evidence.ImagePath is not null)
            {
                content.Add(new { type = "image", source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(await File.ReadAllBytesAsync(evidence.ImagePath, cancellationToken)) } });
            }

            var message = JsonSerializer.Serialize(new
            {
                type = "user",
                session_id = threadId,
                message = new { role = "user", content }
            });
            await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                var type = value.TryGetProperty("type", out var field) ? field.GetString() : null;
                if (type == "user" && value.TryGetProperty("message", out var echoed)
                    && echoed.TryGetProperty("content", out var echoedContent) && echoedContent.ValueKind == JsonValueKind.Array
                    && echoedContent.EnumerateArray().Any(c => c.TryGetProperty("text", out var text) && text.GetString() == evidence.Prompt))
                {
                    update("running", "Claude Code acknowledged the annotation and is working in the selected chat.");
                }

                if (type != "result")
                {
                    continue;
                }

                sawResult = true;
                var denied = value.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array && denials.GetArrayLength() > 0;
                var failed = value.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                update(denied ? "needs_attention" : failed ? "work_failed" : "ready_for_review",
                    denied ? "Claude needs permission to finish. Resume this chat in Claude Code to approve the requested actions."
                        : failed ? "Claude could not finish this turn. Resume the chat for details." : "Claude finished this turn. Review the result in the app.");
            }
            await process.WaitForExitAsync(cancellationToken);
            var errors = await errorTask;
            if (!sawResult)
            {
                throw new IOException("Claude exited without a delivery result" + (string.IsNullOrWhiteSpace(errors) ? "." : ": " + errors));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            { await errorTask; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> DrainErrorsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length < 4096)
            {
                text.Append(buffer, 0, Math.Min(count, 4096 - text.Length));
            }
        }

        return text.ToString().Trim();
    }
}
