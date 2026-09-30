namespace Ansight.Host.Devices;

internal sealed record IosWebDriverAgentOptions(string? ServerUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl);

    public static IosWebDriverAgentOptions Resolve(RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new IosWebDriverAgentOptions(
            FirstNonEmpty(
                options.IosWebDriverAgentServerUrl,
                Environment.GetEnvironmentVariable("ANSIGHT_WDA_SERVER_URL")));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
