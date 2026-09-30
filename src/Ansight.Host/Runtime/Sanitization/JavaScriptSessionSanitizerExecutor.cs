using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed class JavaScriptSessionSanitizerExecutor : IDisposable
{
    private const int MaximumResponseCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan moduleLoadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan invocationTimeout = TimeSpan.FromSeconds(20);
    private static readonly string bootstrapSource = EmbeddedTextResource
        .Read("Runtime/Sanitization/Resources/session-sanitizer-bootstrap.mjs")
        .TrimEnd();

    private readonly object gate = new();
    private readonly Process process;
    private readonly StringBuilder standardError = new();
    private bool disposed;

    public JavaScriptSessionSanitizerExecutor(string modulePath, string configuredExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredExecutablePath);
        var absoluteModulePath = Path.GetFullPath(modulePath.Trim());
        if (!File.Exists(absoluteModulePath))
        {
            throw new FileNotFoundException("The sanitizer module was not found.", absoluteModulePath);
        }

        var extension = Path.GetExtension(absoluteModulePath);
        if (!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Sanitizer modules must use .ts.");
        }

        var runtime = JavaScriptRuntimeResolver.Resolve(configuredExecutablePath);
        if (!runtime.IsAvailable)
        {
            throw new InvalidOperationException(runtime.Message);
        }

        var startInfo = CreateStartInfo(runtime.ExecutablePath, absoluteModulePath, extension);
        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, arguments) =>
        {
            if (arguments.Data is null)
            {
                return;
            }

            lock (standardError)
            {
                if (standardError.Length < 65_536)
                {
                    standardError.AppendLine(arguments.Data);
                }
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The sanitizer Node.js runtime did not start.");
        }

        process.BeginErrorReadLine();
        try
        {
            var handshake = process.StandardOutput.ReadLineAsync()
                .WaitAsync(moduleLoadTimeout)
                .GetAwaiter()
                .GetResult();
            var message = ParseMessage(handshake);
            if (!string.Equals(message["type"]?.GetValue<string>(), "ready", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The sanitizer module did not complete its startup handshake.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SessionSanitizerInvocationResult Invoke(
        string handler,
        JsonObject item,
        JsonObject? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var request = new JsonObject
            {
                ["handler"] = handler,
                ["item"] = item.DeepClone(),
                ["context"] = context?.DeepClone()
            };
            var requestLine = request.ToJsonString(JsonUtil.Compact);
            process.StandardInput.WriteLine(requestLine);
            process.StandardInput.Flush();
            string? responseLine;
            try
            {
                responseLine = process.StandardOutput.ReadLineAsync()
                    .WaitAsync(invocationTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException)
            {
                TryKill();
                throw new TimeoutException($"Sanitizer function '{handler}' exceeded the {invocationTimeout.TotalSeconds:0}-second limit.");
            }

            if (responseLine is null)
            {
                throw new InvalidDataException(
                    $"Sanitizer function '{handler}' exited without a result.{FormatStandardError()}");
            }

            if (responseLine.Length > MaximumResponseCharacters)
            {
                throw new InvalidDataException(
                    $"Sanitizer function '{handler}' returned {responseLine.Length:N0} characters from "
                    + $"a {requestLine.Length:N0}-character request, exceeding the {MaximumResponseCharacters:N0}-character "
                    + "per-item limit. Return a smaller object or null from this handler.");
            }

            var response = ParseMessage(responseLine);
            if (string.Equals(response["type"]?.GetValue<string>(), "error", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Sanitizer function '{handler}' failed: {response["message"]?.GetValue<string>() ?? "Unknown error."}");
            }

            if (!string.Equals(response["type"]?.GetValue<string>(), "result", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Sanitizer function '{handler}' returned an invalid protocol response.");
            }

            return new SessionSanitizerInvocationResult(
                response["value"]?.DeepClone() as JsonObject,
                response["value"] is null,
                response["redactions"]?.GetValue<int>() ?? 0);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(500))
                {
                    TryKill();
                }
            }
            catch (InvalidOperationException)
            {
                // The process did not start or already exited.
            }

            process.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string modulePath, string extension)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(modulePath) ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--experimental-strip-types");
        }

        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("--eval");
        startInfo.ArgumentList.Add(bootstrapSource);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(modulePath);
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var inheritedTemp = Environment.GetEnvironmentVariable("TMPDIR")
                            ?? Environment.GetEnvironmentVariable("TEMP")
                            ?? Path.GetTempPath();
        var inheritedSystemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(inheritedPath))
        {
            startInfo.Environment["PATH"] = inheritedPath;
        }

        startInfo.Environment["TMPDIR"] = inheritedTemp;
        startInfo.Environment["TEMP"] = inheritedTemp;
        startInfo.Environment["TMP"] = inheritedTemp;
        startInfo.Environment["ANSIGHT_SANITIZER"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";
        if (!string.IsNullOrWhiteSpace(inheritedSystemRoot))
        {
            startInfo.Environment["SystemRoot"] = inheritedSystemRoot;
        }

        return startInfo;
    }

    private static JsonObject ParseMessage(string? line)
    {
        try
        {
            return JsonNode.Parse(line ?? string.Empty) as JsonObject
                   ?? throw new InvalidDataException("The sanitizer runtime returned malformed protocol JSON.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The sanitizer runtime returned malformed protocol JSON.", exception);
        }
    }

    private string FormatStandardError()
    {
        lock (standardError)
        {
            return standardError.Length == 0
                ? string.Empty
                : $" Runtime output: {standardError.ToString().Trim()}";
        }
    }

    private void TryKill()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}

internal sealed record SessionSanitizerInvocationResult(
    JsonObject? Value,
    bool WasRemoved,
    int RedactionCount);
