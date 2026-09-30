using System.Diagnostics;

namespace Ansight.Infrastructure.Security;

public sealed class LinuxSecretServiceEncryptedStorage : IEncryptedStorage
{
    private const string IndexKey = "__ansight_cli_keys";
    private const string Service = "Ansight.Cli";
    private const string LegacyIndexKey = "__ansight_studio_keys";
    private const string LegacyService = "Ansight.Studio";
    private readonly Lock gate = new();
    private readonly string secretToolPath;
    private readonly Func<IReadOnlyList<string>, string?, bool, string?>? commandRunner;

    public LinuxSecretServiceEncryptedStorage(string secretToolPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The Secret Service provider is available only on Linux.");
        }

        if (string.IsNullOrWhiteSpace(secretToolPath) || !File.Exists(secretToolPath))
        {
            throw new ArgumentException("A valid secret-tool executable path is required.", nameof(secretToolPath));
        }

        this.secretToolPath = Path.GetFullPath(secretToolPath);
    }

    internal LinuxSecretServiceEncryptedStorage(
        Func<IReadOnlyList<string>, string?, bool, string?> commandRunner)
    {
        this.commandRunner = commandRunner;
        secretToolPath = string.Empty;
    }

    public static bool TryResolveExecutable(out string path)
    {
        path = string.Empty;
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            return false;
        }

        var environmentPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(environmentPath))
        {
            return false;
        }

        foreach (var directory in environmentPath.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "secret-tool");
            if (File.Exists(candidate))
            {
                path = Path.GetFullPath(candidate);
                return true;
            }
        }

        return false;
    }

    /// <summary>Checks the service, not just the presence of secret-tool. Setup may opt into a temporary write probe.</summary>
    public static bool TryProbe(bool verifyWrite, out string message)
    {
        if (!TryResolveExecutable(out var path))
        {
            message = "Linux Secret Service requires secret-tool and a user D-Bus session. Configure an external key with 'ansight config credentials --key-file <path> --non-interactive' on headless hosts.";
            return false;
        }

        try
        {
            var storage = new LinuxSecretServiceEncryptedStorage(path);
            if (verifyWrite)
            {
                var key = "ansight-setup-probe-" + Guid.NewGuid().ToString("N");
                var value = Guid.NewGuid().ToString("N");
                try
                {
                    storage.SetValue(Service, key, value);
                    if (storage.GetValue(Service, key) != value)
                        throw new InvalidOperationException("Secret Service did not return the setup probe.");
                }
                finally { storage.RemoveValue(Service, key); }
            }
            else
            {
                _ = storage.GetValue(Service, IndexKey);
            }
            message = verifyWrite
                ? "Linux Secret Service passed a temporary store, read, and delete probe."
                : "Linux Secret Service responded to a read probe. Run 'ansight config credentials' to verify write access.";
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException
                                        or System.ComponentModel.Win32Exception or IOException)
        {
            message = "Linux Secret Service is unavailable or locked. Unlock the user vault or configure an external key file. " + exception.Message;
            return false;
        }
    }

    public string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (gate)
        {
            return GetValue(Service, key) ?? GetValue(LegacyService, key);
        }
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(key));
        }

        lock (gate)
        {
            if (value is null)
            {
                RemoveValue(Service, key);
                RemoveValue(LegacyService, key);
                RemoveIndexedKey(Service, IndexKey, key);
                RemoveIndexedKey(LegacyService, LegacyIndexKey, key);
                return;
            }

            SetValue(Service, key, value);
            AddIndexedKey(Service, IndexKey, key);
            RemoveValue(LegacyService, key);
            RemoveIndexedKey(LegacyService, LegacyIndexKey, key);
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (gate)
        {
            RemoveValue(Service, key);
            RemoveValue(LegacyService, key);
            RemoveIndexedKey(Service, IndexKey, key);
            RemoveIndexedKey(LegacyService, LegacyIndexKey, key);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            foreach (var key in ReadIndexedKeys(Service, IndexKey))
            {
                RemoveValue(Service, key);
            }

            foreach (var key in ReadIndexedKeys(LegacyService, LegacyIndexKey))
            {
                RemoveValue(LegacyService, key);
            }

            RemoveValue(Service, IndexKey);
            RemoveValue(LegacyService, LegacyIndexKey);
        }
    }

    internal void MigrateLegacyEntries()
    {
        lock (gate)
        {
            foreach (var key in ReadIndexedKeys(LegacyService, LegacyIndexKey))
            {
                var legacyValue = GetValue(LegacyService, key);
                if (legacyValue is not null)
                {
                    var currentValue = GetValue(Service, key);
                    if (currentValue is null)
                    {
                        SetValue(Service, key, legacyValue);
                        if (GetValue(Service, key) != legacyValue)
                        {
                            throw new InvalidOperationException("Linux Secret Service could not verify migrated credential.");
                        }
                    }

                    AddIndexedKey(Service, IndexKey, key);
                    RemoveValue(LegacyService, key);
                }

                RemoveIndexedKey(LegacyService, LegacyIndexKey, key);
            }

            RemoveValue(LegacyService, LegacyIndexKey);
        }
    }

    private string? GetValue(string service, string key)
    {
        var result = Run(
            ["lookup", "service", service, "account", key],
            standardInput: null,
            allowMissing: true);
        if (result is null)
        {
            return null;
        }

        return RemoveTerminatingNewline(result);
    }

    private void SetValue(string service, string key, string value)
    {
        _ = Run(
            [
                "store",
                "--label=Ansight CLI credential",
                "service",
                service,
                "account",
                key
            ],
            value,
            allowMissing: false);
    }

    private void RemoveValue(string service, string key)
    {
        _ = Run(
            ["clear", "service", service, "account", key],
            standardInput: null,
            allowMissing: true);
    }

    private string? Run(
        IReadOnlyList<string> arguments,
        string? standardInput,
        bool allowMissing)
    {
        if (commandRunner is not null)
        {
            return commandRunner(arguments, standardInput, allowMissing);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = secretToolPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start Linux secret-tool.");
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
        }

        process.StandardInput.Close();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("Linux Secret Service did not respond within 30 seconds.");
        }

        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        if (process.ExitCode == 0)
        {
            return standardOutput;
        }

        if (allowMissing && process.ExitCode == 1 && string.IsNullOrWhiteSpace(standardError))
        {
            return null;
        }

        throw new InvalidOperationException(
            $"Linux Secret Service operation failed with exit code {process.ExitCode}: "
            + (string.IsNullOrWhiteSpace(standardError) ? "unknown error" : standardError.Trim()));
    }

    private List<string> ReadIndexedKeys(string service, string indexKey)
    {
        var content = GetValue(service, indexKey);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(content)
                   ?? throw new InvalidOperationException("Linux Secret Service credential index is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Linux Secret Service credential index is invalid.", exception);
        }
    }

    private void AddIndexedKey(string service, string indexKey, string key)
    {
        if (string.Equals(key, indexKey, StringComparison.Ordinal))
        {
            return;
        }

        var keys = ReadIndexedKeys(service, indexKey);
        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
            WriteIndexedKeys(service, indexKey, keys);
        }
    }

    private void RemoveIndexedKey(string service, string indexKey, string key)
    {
        if (string.Equals(key, indexKey, StringComparison.Ordinal))
        {
            return;
        }

        var keys = ReadIndexedKeys(service, indexKey);
        if (keys.Remove(key))
        {
            WriteIndexedKeys(service, indexKey, keys);
        }
    }

    private void WriteIndexedKeys(string service, string indexKey, IEnumerable<string> keys)
    {
        var normalized = keys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        SetValue(service, indexKey, JsonSerializer.Serialize(normalized));
    }

    private static string RemoveTerminatingNewline(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return value[..^2];
        }

        return value.EndsWith('\n') ? value[..^1] : value;
    }
}
