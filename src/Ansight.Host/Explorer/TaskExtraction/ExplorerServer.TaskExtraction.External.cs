using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private sealed record ExternalDraftRequest(string SessionId, string Format, string Source, string? DeviceId);

    private sealed record ExternalDraftResult(string Status, string Message, string Output);

    private async Task<ExternalDraftResult> ReviewExternalDraftAsync(
        ExternalDraftRequest request,
        bool play,
        CancellationToken cancellationToken)
    {
        if (request.Format is not ("maestro" or "appium"))
            throw new InvalidDataException("Choose Maestro or Appium for this draft.");
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 256_000)
            throw new InvalidDataException("Draft source must contain 1 to 256,000 characters.");

        var snapshot = await runtime.Sessions.LoadSnapshotAsync(request.SessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Session '{request.SessionId}' was not found.");
        if (request.Format == "maestro")
        {
            MaestroFlowRefiner.ValidateSource(request.Source, snapshot.AppId);
        }
        else if (!request.Source.Contains(
                     "const appId = " + JsonSerializer.Serialize(snapshot.AppId) + ";",
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Appium script must target the recorded app ID.");
        }

        var workspacePath = runtime.Apps.Get(snapshot.AppId)?.CodebasePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new InvalidDataException($"App '{snapshot.AppId}' must be linked to a workspace before validating or playing a draft.");

        var directoryPath = Path.Combine(workspacePath, request.Format == "maestro" ? ".maestro" : "appium");
        Directory.CreateDirectory(directoryPath);
        var temporaryPath = Path.Combine(directoryPath,
            ".ansight-review-" + Guid.NewGuid().ToString("N") + (request.Format == "maestro" ? ".yaml" : ".test.mjs"));
        try
        {
            await File.WriteAllTextAsync(temporaryPath, request.Source, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            if (request.Format == "maestro" && !play)
                return new ExternalDraftResult("passed", "Maestro app ID and supported commands are valid. Review selectors and add an outcome assertion before playing.", "");

            if (play && string.IsNullOrWhiteSpace(request.DeviceId))
                throw new InvalidDataException("Choose a connected device before playing the draft.");

            var startInfo = new ProcessStartInfo
            {
                FileName = request.Format == "maestro" ? ResolveMaestroExecutable() : ResolveNodeExecutable(),
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (request.Format == "maestro")
            {
                startInfo.ArgumentList.Add("--device");
                startInfo.ArgumentList.Add(request.DeviceId!);
                startInfo.ArgumentList.Add("test");
            }
            else
            {
                startInfo.ArgumentList.Add(play ? "--test" : "--check");
                if (play) startInfo.Environment["APPIUM_UDID"] = request.DeviceId;
            }
            startInfo.ArgumentList.Add(temporaryPath);
            return await RunExternalDraftProcessAsync(startInfo, request.Format, play, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ResolveMaestroExecutable()
    {
        if (OperatingSystem.IsWindows()) return "maestro";
        var userInstallation = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".maestro", "bin", "maestro");
        return File.Exists(userInstallation) ? userInstallation : "maestro";
    }

    private string ResolveNodeExecutable()
    {
        var resolution = JavaScriptRuntimeResolver.Resolve(runtime.JavaScriptExecutablePath);
        if (!resolution.IsAvailable) throw new InvalidDataException(resolution.Message);
        return resolution.ExecutablePath;
    }

    private static async Task<ExternalDraftResult> RunExternalDraftProcessAsync(
        ProcessStartInfo startInfo,
        string format,
        bool play,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(play ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(20));
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = string.Join("\n", new[] { await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false) }
                    .Where(static part => !string.IsNullOrWhiteSpace(part))).Trim();
                if (output.Length > 16_000) output = output[^16_000..];
                var passed = process.ExitCode == 0;
                return new ExternalDraftResult(
                    passed ? "passed" : "failed",
                    passed
                        ? play ? $"{format} test passed on the selected device." : "Appium JavaScript syntax is valid. Review locators and add an outcome assertion before playing."
                        : $"{format} {(play ? "test" : "validation")} exited with code {process.ExitCode}.",
                    output);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Win32Exception error)
        {
            return new ExternalDraftResult("failed", $"{startInfo.FileName} is unavailable: {error.Message}", "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ExternalDraftResult("failed", $"{format} {(play ? "test" : "validation")} timed out.", "");
        }
    }
}
