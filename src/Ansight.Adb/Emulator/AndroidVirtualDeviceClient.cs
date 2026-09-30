using Ansight.Adb.Devices;
using Ansight.Adb.Emulator;

namespace Ansight.Adb;

public sealed class AndroidVirtualDeviceClient
{
    private readonly AndroidEmulatorToolResolution toolResolution;
    private readonly IProcessRunner processRunner;
    private readonly string androidVirtualDeviceHome;

    public AndroidVirtualDeviceClient(AndroidEmulatorToolResolution toolResolution)
        : this(toolResolution, new DotNetProcessRunner())
    {
    }

    public AndroidVirtualDeviceClient(
        AndroidEmulatorToolResolution toolResolution,
        IAdbProcessLauncher processLauncher)
        : this(toolResolution, new AdbLauncherProcessRunner(processLauncher))
    {
    }

    internal AndroidVirtualDeviceClient(
        AndroidEmulatorToolResolution toolResolution,
        IProcessRunner processRunner,
        string? androidVirtualDeviceHome = null)
    {
        this.toolResolution = toolResolution ?? throw new ArgumentNullException(nameof(toolResolution));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.androidVirtualDeviceHome = androidVirtualDeviceHome
            ?? ResolveAndroidVirtualDeviceHome();
        if (!toolResolution.IsFound)
        {
            throw new ArgumentException("A resolved Android Emulator tool is required.", nameof(toolResolution));
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            toolResolution.EmulatorPath,
            ["-list-avds"],
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(BuildFailureMessage("list Android virtual devices", result));
        }

        return ParseAvdNames(result.StandardOutput);
    }

    public Task<int> LaunchAsync(
        string avdName,
        CancellationToken cancellationToken = default)
        => LaunchAsync(
            avdName,
            new AndroidVirtualDeviceLaunchOptions(),
            cancellationToken);

    public Task<int> LaunchAsync(
        string avdName,
        AndroidVirtualDeviceLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSystemImage(avdName.Trim());
        var arguments = new List<string> { "-avd", avdName.Trim() };
        if (options.Headless)
        {
            arguments.Add("-no-window");
        }

        var processId = processRunner.Launch(
            toolResolution.EmulatorPath,
            arguments);
        return Task.FromResult(processId);
    }

    public AndroidDeviceFormFactor GetDeviceFormFactor(string avdName)
    {
        var avdDirectory = ResolveAvdDirectory(avdName);
        if (string.IsNullOrWhiteSpace(avdDirectory))
        {
            return AndroidDeviceFormFactor.Unknown;
        }

        var configPath = Path.Combine(avdDirectory, "config.ini");
        return FormFactorDetector.FromAvdConfiguration(
            ReadProperty(configPath, "hw.device.name"),
            ReadProperty(configPath, "tag.id"),
            ReadProperty(configPath, "hw.lcd.width"),
            ReadProperty(configPath, "hw.lcd.height"),
            ReadProperty(configPath, "hw.lcd.density"));
    }

    internal static IReadOnlyList<string> ParseAvdNames(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !value.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            .Where(static value => !value.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildFailureMessage(string operation, AdbCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"Android Emulator failed to {operation} with exit code {result.ExitCode}."
            : $"Android Emulator failed to {operation}: {detail}";
    }

    private void ValidateSystemImage(string avdName)
    {
        var avdDirectory = ResolveAvdDirectory(avdName);
        if (string.IsNullOrWhiteSpace(avdDirectory))
        {
            return;
        }

        var configPath = Path.Combine(avdDirectory, "config.ini");
        var relativeSystemImagePath = ReadProperty(configPath, "image.sysdir.1");
        if (string.IsNullOrWhiteSpace(relativeSystemImagePath))
        {
            return;
        }

        var sdkRoot = Directory.GetParent(
            Path.GetDirectoryName(toolResolution.EmulatorPath) ?? string.Empty)?.FullName;
        if (string.IsNullOrWhiteSpace(sdkRoot))
        {
            return;
        }

        var systemImagePath = Path.IsPathRooted(relativeSystemImagePath)
            ? relativeSystemImagePath
            : Path.Combine(
                sdkRoot,
                relativeSystemImagePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(systemImagePath))
        {
            return;
        }

        var packageName = relativeSystemImagePath
            .Trim()
            .TrimEnd('/', '\\')
            .Replace('/', ';')
            .Replace('\\', ';');
        throw new InvalidOperationException(
            $"Android virtual device '{avdName}' cannot start because its system image is missing. "
            + $"Install '{packageName}' in Android SDK Manager, then retry.");
    }

    private string? ResolveAvdDirectory(string avdName)
    {
        if (string.IsNullOrWhiteSpace(androidVirtualDeviceHome)
            || !string.Equals(Path.GetFileName(avdName), avdName, StringComparison.Ordinal))
        {
            return null;
        }

        var avdDefinitionPath = Path.Combine(androidVirtualDeviceHome, $"{avdName}.ini");
        if (!File.Exists(avdDefinitionPath))
        {
            return null;
        }

        var avdDirectory = ReadProperty(avdDefinitionPath, "path");
        if (!string.IsNullOrWhiteSpace(avdDirectory))
        {
            return Path.GetFullPath(avdDirectory);
        }

        var relativePath = ReadProperty(avdDefinitionPath, "path.rel");
        return string.IsNullOrWhiteSpace(relativePath)
            ? null
            : Path.GetFullPath(Path.Combine(androidVirtualDeviceHome, "..", relativePath));
    }

    private static string? ReadProperty(string path, string propertyName)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var prefix = propertyName + "=";
        return File.ReadLines(path)
            .Select(static line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..]
            .Trim();
    }

    private static string ResolveAndroidVirtualDeviceHome()
    {
        var explicitAvdHome = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
        if (!string.IsNullOrWhiteSpace(explicitAvdHome))
        {
            return Path.GetFullPath(explicitAvdHome.Trim());
        }

        var androidUserHome = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
        if (!string.IsNullOrWhiteSpace(androidUserHome))
        {
            return Path.Combine(Path.GetFullPath(androidUserHome.Trim()), "avd");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, ".android", "avd");
    }
}
