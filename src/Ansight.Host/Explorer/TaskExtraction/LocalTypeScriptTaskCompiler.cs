using System.Diagnostics;
using System.Text;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Explorer.TaskExtraction;

internal static class LocalTypeScriptTaskCompiler
{
    private const int MaximumDiagnosticCharacters = 32_000;
    internal const string MissingCompilerMessage =
        "The TypeScript compiler was not found. Install TypeScript so 'tsc' is on PATH, or set ANSIGHT_TSC_PATH; generated tasks cannot be accepted without a real compile against ansight-task.d.ts.";
    internal const string MissingRuntimeMessage =
        "The Node.js runtime needed for TypeScript validation was not found. Install Node.js so 'node' is on PATH, or configure the Ansight Node.js runtime path.";
    private static readonly TimeSpan compilationTimeout = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<string> Validate(
        string taskDirectory,
        string sourcePath,
        string typeDefinitions,
        string javaScriptExecutablePath = "node")
        => ValidateDetailed(
            taskDirectory,
            sourcePath,
            typeDefinitions,
            javaScriptExecutablePath).Diagnostics;

    public static LocalTypeScriptTaskValidationResult ValidateDetailed(
        string taskDirectory,
        string sourcePath,
        string typeDefinitions,
        string javaScriptExecutablePath = "node")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeDefinitions);

        var compilerPath = ResolveCompilerPath();
        if (compilerPath is null)
        {
            return LocalTypeScriptTaskValidationResult.InfrastructureFailure(MissingCompilerMessage);
        }

        var runtimePath = ResolveJavaScriptRuntimePath(javaScriptExecutablePath);
        if (!OperatingSystem.IsWindows() && runtimePath is null)
        {
            return LocalTypeScriptTaskValidationResult.InfrastructureFailure(MissingRuntimeMessage);
        }

        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, "ansight-task.js"),
            RepositoryModuleContractArtifacts.GetTaskRuntimeModule(),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(taskDirectory, "ansight-task.d.ts"),
            typeDefinitions.TrimEnd() + Environment.NewLine,
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(taskDirectory, "package.json"),
            "{\n  \"private\": true,\n  \"type\": \"module\"\n}\n",
            new UTF8Encoding(false));
        var configurationPath = Path.Combine(taskDirectory, "tsconfig.extraction.json");
        var configuration = $$"""
                              {
                                "compilerOptions": {
                                  "allowImportingTsExtensions": true,
                                  "module": "NodeNext",
                                  "moduleResolution": "NodeNext",
                                  "noEmit": true,
                                  "strict": true,
                                  "target": "ES2022",
                                  "verbatimModuleSyntax": true
                                },
                                "include": ["**/*.ts", "**/*.d.ts"]
                              }
                              """;
        File.WriteAllText(
            configurationPath,
            configuration + Environment.NewLine,
            new UTF8Encoding(false));

        Process? process;
        try
        {
            process = Process.Start(CreateStartInfo(compilerPath, configurationPath, runtimePath));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return LocalTypeScriptTaskValidationResult.InfrastructureFailure(
                $"The TypeScript compiler did not start: {exception.Message}");
        }
        if (process is null)
        {
            return LocalTypeScriptTaskValidationResult.InfrastructureFailure(
                "The TypeScript compiler process did not start.");
        }
        using var runningProcess = process;

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)compilationTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The compiler exited between the timeout and kill attempt.
            }

            return LocalTypeScriptTaskValidationResult.InfrastructureFailure(
                $"TypeScript validation exceeded the {compilationTimeout.TotalSeconds:0}-second limit.");
        }

        Task.WaitAll(standardOutputTask, standardErrorTask);
        if (process.ExitCode == 0)
        {
            return LocalTypeScriptTaskValidationResult.Success;
        }

        var diagnostics = string.Join(
                Environment.NewLine,
                new[] { standardOutputTask.Result, standardErrorTask.Result }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (diagnostics.Length > MaximumDiagnosticCharacters)
        {
            diagnostics = diagnostics[..MaximumDiagnosticCharacters] + Environment.NewLine + "…";
        }

        return LocalTypeScriptTaskValidationResult.SourceFailure(
            string.IsNullOrWhiteSpace(diagnostics)
                ? $"TypeScript compilation failed with exit code {process.ExitCode}."
                : "TypeScript compilation failed against the bundled ansight-task.d.ts:" + Environment.NewLine + diagnostics);
    }

    internal static string? GetEnvironmentError(
        string javaScriptExecutablePath,
        string? inheritedPath = null,
        string? configuredCompilerPath = null)
    {
        if (ResolveCompilerPath(configuredCompilerPath, inheritedPath) is null)
        {
            return MissingCompilerMessage;
        }

        return !OperatingSystem.IsWindows()
               && ResolveJavaScriptRuntimePath(javaScriptExecutablePath, inheritedPath) is null
            ? MissingRuntimeMessage
            : null;
    }

    internal static string? ResolveCompilerPath(
        string? configuredPath = null,
        string? inheritedPath = null)
    {
        var requestedPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Environment.GetEnvironmentVariable("ANSIGHT_TSC_PATH")
            : configuredPath;
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            var normalizedPath = Path.GetFullPath(requestedPath.Trim());
            return File.Exists(normalizedPath) ? normalizedPath : null;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "tsc.cmd", "tsc.exe", "tsc" }
            : new[] { "tsc" };
        foreach (var directory in (inheritedPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidatePath = Path.Combine(directory, executableName);
                if (File.Exists(candidatePath))
                {
                    return Path.GetFullPath(candidatePath);
                }
            }
        }

        var commonPaths = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm",
                    "tsc.cmd")
            }
            : new[] { "/opt/homebrew/bin/tsc", "/usr/local/bin/tsc" };
        foreach (var candidatePath in commonPaths)
        {
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        return null;
    }

    internal static string? ResolveJavaScriptRuntimePath(
        string configuredPath,
        string? inheritedPath = null)
    {
        var resolution = JavaScriptRuntimeResolver.Resolve(configuredPath, inheritedPath);
        return resolution.IsAvailable ? resolution.ExecutablePath : null;
    }

    internal static ProcessStartInfo CreateStartInfo(
        string compilerPath,
        string configurationPath,
        string? javaScriptRuntimePath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows()
            && string.Equals(Path.GetExtension(compilerPath), ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(compilerPath);
        }
        else if (!OperatingSystem.IsWindows())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(javaScriptRuntimePath);
            startInfo = new ProcessStartInfo { FileName = javaScriptRuntimePath };
            startInfo.ArgumentList.Add(compilerPath);
        }
        else
        {
            startInfo = new ProcessStartInfo { FileName = compilerPath };
        }

        startInfo.WorkingDirectory = Path.GetDirectoryName(configurationPath)!;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.ArgumentList.Add("--pretty");
        startInfo.ArgumentList.Add("false");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(configurationPath);
        return startInfo;
    }
}
