using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed partial class AppExecutionArtifactIdentifierResolver
{
    private readonly IAppExecutionArtifactToolRunner toolRunner;
    private readonly string? aaptPathOverride;

    public AppExecutionArtifactIdentifierResolver()
        : this(new DotNetAppExecutionArtifactToolRunner(), aaptPathOverride: null)
    {
    }

    internal AppExecutionArtifactIdentifierResolver(
        IAppExecutionArtifactToolRunner toolRunner,
        string? aaptPathOverride)
    {
        this.toolRunner = toolRunner ?? throw new ArgumentNullException(nameof(toolRunner));
        this.aaptPathOverride = aaptPathOverride;
    }

    public async Task<AppExecutionArtifactIdentifierResolution> ResolveAsync(
        string artifactPath,
        string? configuredAdbPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return AppExecutionArtifactIdentifierResolution.Failure(
                "An .app, .ipa, or .apk artifact path is required.");
        }

        try
        {
            using var package = WorkspaceTestApplicationPackage.Open(artifactPath);
            return package.Platform switch
            {
                DevicePlatforms.Ios => await ResolveAppleIdentifierAsync(
                        package.InstallPath,
                        cancellationToken)
                    .ConfigureAwait(false),
                DevicePlatforms.Android => await ResolveAndroidIdentifierAsync(
                        package.InstallPath,
                        configuredAdbPath,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => AppExecutionArtifactIdentifierResolution.Failure(
                    $"The artifact platform '{package.Platform}' is not supported for app execution.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or DirectoryNotFoundException
                                           or FileNotFoundException
                                           or InvalidDataException
                                           or InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or Win32Exception)
        {
            return AppExecutionArtifactIdentifierResolution.Failure(
                $"Ansight could not inspect the app artifact: {exception.Message} "
                + "Pass --app-id <id> to provide the bundle or package identifier explicitly.");
        }
    }

    private static async Task<AppExecutionArtifactIdentifierResolution> ResolveAppleIdentifierAsync(
        string appBundlePath,
        CancellationToken cancellationToken)
    {
        var infoPlistPath = Path.Combine(appBundlePath, "Info.plist");
        if (!File.Exists(infoPlistPath))
        {
            return AppExecutionArtifactIdentifierResolution.Failure(
                $"The Apple app bundle '{appBundlePath}' does not contain Info.plist. "
                + "Pass --app-id <id> to provide CFBundleIdentifier explicitly.");
        }

        var data = await File.ReadAllBytesAsync(infoPlistPath, cancellationToken).ConfigureAwait(false);
        var preview = PropertyListParser.Parse(data);
        var document = XDocument.Parse(preview.SourceText, LoadOptions.None);
        var dictionary = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "dict");
        var elements = dictionary?.Elements().ToArray() ?? [];
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name.LocalName == "key"
                && elements[index].Value == "CFBundleIdentifier"
                && elements[index + 1].Name.LocalName == "string"
                && !string.IsNullOrWhiteSpace(elements[index + 1].Value))
            {
                return AppExecutionArtifactIdentifierResolution.Success(
                    elements[index + 1].Value.Trim());
            }
        }

        return AppExecutionArtifactIdentifierResolution.Failure(
            "The Apple app bundle does not contain a readable CFBundleIdentifier. "
            + "Pass --app-id <id> to provide it explicitly.");
    }

    private async Task<AppExecutionArtifactIdentifierResolution> ResolveAndroidIdentifierAsync(
        string apkPath,
        string? configuredAdbPath,
        CancellationToken cancellationToken)
    {
        var aaptPath = ResolveAaptPath(configuredAdbPath);
        if (string.IsNullOrWhiteSpace(aaptPath))
        {
            return AppExecutionArtifactIdentifierResolution.Failure(
                "Android SDK Build-Tools could not be found to inspect the APK. "
                + "Install Android SDK Build-Tools, configure --adb-path, or pass --app-id <id> explicitly.");
        }

        var result = await toolRunner.RunAsync(
                aaptPath,
                ["dump", "badging", apkPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();
            return AppExecutionArtifactIdentifierResolution.Failure(
                $"Android Build-Tools could not read the APK manifest: {detail}. "
                + "Pass --app-id <id> to provide the package identifier explicitly.");
        }

        var match = ApkPackagePattern().Match(result.StandardOutput);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["id"].Value)
            ? AppExecutionArtifactIdentifierResolution.Success(match.Groups["id"].Value)
            : AppExecutionArtifactIdentifierResolution.Failure(
                "The APK manifest does not contain a readable package identifier. "
                + "Pass --app-id <id> to provide it explicitly.");
    }

    internal string? ResolveAaptPath(string? configuredAdbPath)
    {
        if (!string.IsNullOrWhiteSpace(aaptPathOverride))
        {
            return aaptPathOverride;
        }

        foreach (var sdkRoot in EnumerateAndroidSdkRoots(configuredAdbPath))
        {
            var buildToolsPath = Path.Combine(sdkRoot, "build-tools");
            if (!Directory.Exists(buildToolsPath))
            {
                continue;
            }

            var executableName = OperatingSystem.IsWindows() ? "aapt.exe" : "aapt";
            var candidate = Directory.GetDirectories(buildToolsPath)
                .Select(directoryPath => new AppExecutionArtifactToolCandidate(
                    Path.Combine(directoryPath, executableName),
                    ParseVersion(Path.GetFileName(directoryPath))))
                .Where(static item => File.Exists(item.Path))
                .OrderByDescending(static item => item.Version)
                .ThenByDescending(static item => item.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is not null)
            {
                return candidate.Path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAndroidSdkRoots(string? configuredAdbPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredAdbPath))
        {
            var platformToolsPath = Path.GetDirectoryName(Path.GetFullPath(configuredAdbPath.Trim()));
            var sdkRoot = Directory.GetParent(platformToolsPath ?? string.Empty)?.FullName;
            if (!string.IsNullOrWhiteSpace(sdkRoot))
            {
                candidates.Add(sdkRoot);
            }
        }

        foreach (var variableName in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.Add(value.Trim());
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Library", "Android", "sdk"));
            candidates.Add(Path.Combine(userProfile, "Android", "Sdk"));
            candidates.Add(Path.Combine(userProfile, "AppData", "Local", "Android", "Sdk"));
        }

        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    }

    private static Version ParseVersion(string value)
        => Version.TryParse(value.Split('-', 2)[0], out var version)
            ? version
            : new Version();

    [GeneratedRegex(@"^package:\s+name='(?<id>[^']+)'", RegexOptions.Multiline)]
    private static partial Regex ApkPackagePattern();
}

internal sealed record AppExecutionArtifactIdentifierResolution(
    bool IsSuccess,
    string? ApplicationIdentifier,
    string Message)
{
    public static AppExecutionArtifactIdentifierResolution Success(string applicationIdentifier)
        => new(
            true,
            applicationIdentifier,
            $"Identified app ID '{applicationIdentifier}'.");

    public static AppExecutionArtifactIdentifierResolution Failure(string message)
        => new(false, ApplicationIdentifier: null, message);
}

internal interface IAppExecutionArtifactToolRunner
{
    Task<AppExecutionArtifactToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed record AppExecutionArtifactToolResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

internal sealed class DotNetAppExecutionArtifactToolRunner : IAppExecutionArtifactToolRunner
{
    public async Task<AppExecutionArtifactToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new AppExecutionArtifactToolResult(
                process.ExitCode,
                outputTask.Result,
                errorTask.Result);
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

            throw;
        }
    }
}
