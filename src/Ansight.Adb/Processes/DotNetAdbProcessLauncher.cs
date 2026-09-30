using System.Diagnostics;

namespace Ansight.Adb;

public sealed class DotNetAdbProcessLauncher : IAdbProcessLauncher
{
    public IAdbProcess Start(AdbProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ADB could not be started.");
        return new DotNetAdbProcess(process);
    }

    private sealed class DotNetAdbProcess : IAdbProcess
    {
        private readonly Process process;
        private readonly Task<int> completion;
        private bool disposed;

        public DotNetAdbProcess(Process process)
        {
            this.process = process ?? throw new ArgumentNullException(nameof(process));
            completion = WaitForExitAsync();
        }

        public int ProcessId => process.Id;

        public Stream StandardInput => process.StandardInput.BaseStream;

        public Stream StandardOutput => process.StandardOutput.BaseStream;

        public Stream StandardError => process.StandardError.BaseStream;

        public Task<int> Completion => completion;

        public void Terminate(bool force = false)
        {
            if (disposed || completion.IsCompleted)
            {
                return;
            }
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // This exception is an expected fallback for the best-effort operation.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
                // Callers close stdin once they have supplied all input. Process cleanup remains idempotent.
            }
            if (!completion.IsCompleted)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await completion.ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // This exception is an expected fallback for the best-effort operation.
                }
            }
            process.Dispose();
        }

        private async Task<int> WaitForExitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
