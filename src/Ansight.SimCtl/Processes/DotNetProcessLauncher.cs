using System.Diagnostics;
using Ansight.SimCtl;

namespace Ansight.SimCtl.Processes;

internal sealed class DotNetProcessLauncher : ISimCtlProcessLauncher
{
    public ISimCtlProcess Start(SimCtlProcessStartRequest request)
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
        startInfo.Environment["DEVELOPER_DIR"] = request.DeveloperDirectory;
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("SimCtl could not be started.");
        return new DotNetSimCtlProcess(process);
    }

    private sealed class DotNetSimCtlProcess : ISimCtlProcess
    {
        private readonly Process process;
        private readonly Stream standardInput;
        private readonly Task<int> completion;
        private bool disposed;

        public DotNetSimCtlProcess(Process process)
        {
            this.process = process ?? throw new ArgumentNullException(nameof(process));
            standardInput = process.StandardInput.BaseStream;
            completion = WaitForExitAsync();
        }

        public int ProcessId => process.Id;

        public Stream StandardInput => standardInput;

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
                // Callers consume the raw stream and may already have closed it to
                // send EOF. Closing the StreamWriter instead would flush that closed
                // pipe and mask a successful launch (or its original failure).
                standardInput.Dispose();
                if (!completion.IsCompleted)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await completion.ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // The child may have exited between the completion check and kill.
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private async Task<int> WaitForExitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }
}
