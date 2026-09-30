using System.Diagnostics;

namespace Ansight.Host.Runtime.DeviceExecution;

internal sealed record DeviceCommandResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output).Trim();
    public string RequireText()
        => ExitCode == 0 ? Text : throw new IOException(string.IsNullOrWhiteSpace(Error) ? Text : Error);
}

internal interface IDeviceCommandRunner
{
    Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, int maximumBytes = 1_048_576);
}

internal sealed class DeviceCommandRunner : IDeviceCommandRunner
{
    public async Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, int maximumBytes = 1_048_576)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        try
        {
            var output = ReadAsync(process.StandardOutput.BaseStream, maximumBytes, deadline.Token);
            var error = ReadAsync(process.StandardError.BaseStream, 65_536, deadline.Token);
            // Kill on the first failed reader so the other pipe cannot block indefinitely.
            foreach (var reader in new[] { output, error })
                _ = reader.ContinueWith(_ => Kill(process), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new DeviceCommandResult(process.ExitCode, await output.ConfigureAwait(false),
                Encoding.UTF8.GetString(await error.ConfigureAwait(false)).Trim());
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > limit) throw new IOException($"Device output exceeded {limit} bytes.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
