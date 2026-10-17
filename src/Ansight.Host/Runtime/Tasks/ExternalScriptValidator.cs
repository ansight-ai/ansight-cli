using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.Automation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ansight.Host.Runtime.Tasks;

internal static class ExternalScriptValidator
{
    internal static async Task ValidateAsync(string format, string source, string appId,
        string? javaScriptExecutablePath, CancellationToken cancellationToken)
    {
        ValidateLength(source);
        if (format == "maestro")
        {
            ValidateMaestro(source, appId);
            return;
        }
        if (format != "appium") throw new InvalidDataException("Unknown script format.");
        ValidateAppiumTarget(source, appId);
        var resolution = JavaScriptRuntimeResolver.Resolve(string.IsNullOrWhiteSpace(javaScriptExecutablePath) ? "node" : javaScriptExecutablePath);
        if (!resolution.IsAvailable) throw new InvalidOperationException(resolution.Message);
        var path = Path.Combine(Path.GetTempPath(), $"ansight-appium-check-{Guid.NewGuid():N}.mjs");
        try
        {
            await File.WriteAllTextAsync(path, source, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo(resolution.ExecutablePath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            // Parse only: generated JavaScript is never executed during extraction.
            startInfo.ArgumentList.Add("--check");
            startInfo.ArgumentList.Add(path);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start JavaScript validation.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await output.ConfigureAwait(false);
                var diagnostics = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidDataException("Appium JavaScript syntax is invalid: " + diagnostics[..Math.Min(diagnostics.Length, 4_000)]);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("JavaScript validation could not start: " + error.Message, error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static void ValidateAppiumTarget(string source, string appId)
    {
        ValidateLength(source);
        if (!Regex.IsMatch(source, @"(?m)^const appId = " + Regex.Escape(JsonSerializer.Serialize(appId)) + @";\s*$"))
            throw new InvalidDataException("The Appium script must preserve the recorded const appId declaration.");
    }

    internal static void ValidateMaestro(string source, string appId)
    {
        ValidateLength(source);
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(source)); }
        catch (YamlException error) { throw new InvalidDataException("Invalid Maestro YAML: " + error.Message, error); }
        if (yaml.Documents.Count != 2
            || yaml.Documents[0].RootNode is not YamlMappingNode header
            || !header.Children.TryGetValue(new YamlScalarNode("appId"), out var target)
            || target is not YamlScalarNode app || app.Value != appId)
            throw new InvalidDataException("The Maestro flow must target the recorded app ID and include a YAML command separator.");
        if (yaml.Documents[1].RootNode is not YamlSequenceNode commands || commands.Children.Count == 0)
            throw new InvalidDataException("The Maestro flow needs a command sequence.");
        if (header.Children.Keys.Any(key => key is not YamlScalarNode scalar || scalar.Value is not ("appId" or "name" or "tags")))
            throw new InvalidDataException("The Maestro header supports only appId, name, and tags.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "launchApp", "tapOn", "swipe", "eraseText", "inputText", "assertVisible", "assertNotVisible",
            "extendedWaitUntil", "hideKeyboard", "back", "scrollUntilVisible"
        };
        var hasTestStep = false;
        foreach (var item in commands.Children)
        {
            var command = item switch
            {
                YamlScalarNode scalar => scalar.Value,
                YamlMappingNode mapping when mapping.Children.Count == 1 => (mapping.Children.Keys.Single() as YamlScalarNode)?.Value,
                _ => null
            };
            if (command is null || !allowed.Contains(command))
                throw new InvalidDataException($"Unsupported Maestro command '{command ?? item.ToString()}' in the draft.");
            hasTestStep |= command != "launchApp";
        }
        if (!hasTestStep) throw new InvalidDataException("The Maestro flow needs at least one action or assertion.");
    }

    private static void ValidateLength(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256_000)
            throw new InvalidDataException("Test source must contain 1 to 256,000 characters.");
    }
}
