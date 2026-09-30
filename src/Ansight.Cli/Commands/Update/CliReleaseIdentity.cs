using System.Reflection;

namespace Ansight.Cli.Commands.Update;

internal sealed record CliReleaseIdentity(
    string Version,
    long BuildNumber,
    string InformationalVersion,
    string CommitSha)
{
    public static CliReleaseIdentity Current { get; } = FromAssembly(typeof(CliReleaseIdentity).Assembly);

    internal static CliReleaseIdentity FromAssembly(Assembly assembly)
    {
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static attribute => !string.IsNullOrWhiteSpace(attribute.Key))
            .ToDictionary(
                static attribute => attribute.Key,
                static attribute => attribute.Value ?? string.Empty,
                StringComparer.Ordinal);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = metadata.GetValueOrDefault("AnsightCliVersion");
        if (string.IsNullOrWhiteSpace(version))
        {
            version = informationalVersion?.Split('+', 2)[0]
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0-dev";
        }

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            informationalVersion = version;
        }

        var commitSha = metadata.GetValueOrDefault("AnsightCommitSha");
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            commitSha = informationalVersion.Contains('+', StringComparison.Ordinal)
                ? informationalVersion.Split('+', 2)[1]
                : "unknown";
        }

        var buildNumberSource = metadata.GetValueOrDefault("AnsightCliBuildNumber");
        var buildNumber = long.TryParse(
            buildNumberSource,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedBuildNumber)
            ? parsedBuildNumber
            : 0;

        return new CliReleaseIdentity(version, buildNumber, informationalVersion, commitSha);
    }
}
