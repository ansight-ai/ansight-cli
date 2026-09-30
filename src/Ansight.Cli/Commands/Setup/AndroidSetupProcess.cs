using System.Diagnostics;

namespace Ansight.Cli.Commands.Setup;

internal sealed record AndroidSetupProcessResult(int ExitCode, string Output);

internal static class AndroidSetupProcess
{
    internal static async Task<AndroidSetupProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string? javaPath,
        string? standardInput, CancellationToken token, TimeSpan? timeoutOverride = null, string? sdkRoot = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (javaPath is not null)
        {
            var resolvedJava = new FileInfo(javaPath).ResolveLinkTarget(true)?.FullName ?? javaPath;
            process.StartInfo.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(resolvedJava))!;
        }
        if (sdkRoot is not null)
        {
            process.StartInfo.Environment["ANDROID_HOME"] = sdkRoot;
            process.StartInfo.Environment["ANDROID_SDK_ROOT"] = sdkRoot;
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutOverride ?? TimeSpan.FromMinutes(20));
        try
        {
            if (standardInput is not null)
            {
                try { await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token); }
                catch (IOException) { /* Some tools exit without reading stdin when there is no prompt. */ }
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, (await stdout + "\n" + await stderr).Trim());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
