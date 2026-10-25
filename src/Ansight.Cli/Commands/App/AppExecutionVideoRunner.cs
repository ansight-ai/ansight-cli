namespace Ansight.Cli.Commands.App;

internal static class AppExecutionVideoRunner
{
    internal static async Task<SimulatorAgentRunResult> RunAsync(
        Func<Task<TestVideoCapture?>> startRecording,
        Func<Task<SimulatorAgentRunResult>> execute,
        Func<SimulatorAgentRunAudit, SimulatorAgentAuditPersistenceResult> persistAudit,
        CliOutput output,
        Func<Task>? recordingStopped = null)
    {
        var capture = await startRecording().ConfigureAwait(false);
        SimulatorAgentRunResult? result = null;
        try
        {
            result = await execute().ConfigureAwait(false);
        }
        finally
        {
            if (capture is not null) await capture.StopAsync().ConfigureAwait(false);
            if (recordingStopped is not null) await recordingStopped().ConfigureAwait(false);
            if (capture is not null)
            {
                output.WriteProgress("[video.finalizing] Finalizing video recording.");
                // Complete without the execution token, including cancellation and failures before an audit exists.
                var video = await capture.CompleteAsync(result?.Status == SimulatorAgentRunStatus.Succeeded)
                    .ConfigureAwait(false);
                output.WriteProgress(RunVideoOptions.Render(video).TrimStart());
                if (result is not null)
                {
                    var audit = result.Audit with { Video = video };
                    result = result with { Audit = audit };
                    try
                    {
                        var saved = persistAudit(audit);
                        result = result with
                        {
                            AuditFilePath = saved.FilePath ?? result.AuditFilePath,
                            AuditPersistenceError = saved.ErrorMessage ?? result.AuditPersistenceError
                        };
                    }
                    catch (Exception exception)
                    {
                        // Retained video and the execution outcome remain usable if audit persistence fails.
                        result = result with { AuditPersistenceError = exception.Message };
                    }
                }
            }
        }

        return result;
    }
}
