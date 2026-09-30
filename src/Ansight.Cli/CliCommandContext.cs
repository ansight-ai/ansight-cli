using Ansight.Host;

namespace Ansight.Cli;

internal sealed class CliCommandContext
{
    private static readonly AsyncLocal<CliCommandContext?> AmbientContext = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> AmbientSecrets = new();

    private CliCommandContext(
        RuntimeCoordinator runtime,
        string dataDirectory,
        string? secretValue,
        IReadOnlyDictionary<string, string>? secretValues)
    {
        Runtime = runtime;
        DataDirectory = dataDirectory;
        SecretValue = secretValue;
        SecretValues = secretValues is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(secretValues, StringComparer.Ordinal);
    }

    public static CliCommandContext? Current => AmbientContext.Value;

    public RuntimeCoordinator Runtime { get; }

    public string DataDirectory { get; }

    public string? SecretValue { get; }

    public IReadOnlyDictionary<string, string> SecretValues { get; }

    public string? ResolveSecret(string alias)
        => SecretValues.TryGetValue(alias, out var value)
            ? value
            : ResolveScopedSecret(alias);

    public static string? ResolveScopedSecret(string alias)
        => AmbientSecrets.Value is { } values && values.TryGetValue(alias, out var value)
            ? value
            : Environment.GetEnvironmentVariable(alias);

    public static IDisposable PushSecretValues(IReadOnlyDictionary<string, string> values)
    {
        var previous = AmbientSecrets.Value;
        AmbientSecrets.Value = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        return new CliCommandContextScope(() => AmbientSecrets.Value = previous);
    }

    public static IDisposable Push(
        RuntimeCoordinator runtime,
        string dataDirectory,
        string? secretValue,
        IReadOnlyDictionary<string, string>? secretValues = null)
    {
        var previous = AmbientContext.Value;
        AmbientContext.Value = new CliCommandContext(runtime, dataDirectory, secretValue, secretValues);
        return new CliCommandContextScope(() => AmbientContext.Value = previous);
    }
}
