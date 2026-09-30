using System.Globalization;

namespace Ansight.Cli;

internal sealed class CliArguments
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "help",
        "ai",
        "yes",
        "non-interactive",
        "credentials-only",
        "accept-android-licenses",
        "full",
        "include-secret-metadata",
        "headless",
        "no-headless",
        "close-app-on-completion",
        "beta",
        "json",
        "jsonl",
        "silent",
        "verbose",
        "diagnostic",
        "audit",
        "stdin",
        "connected",
        "exclude-native-logs",
        "sanitize",
        "no-sanitize",
        "overwrite",
        "force",
        "apply",
        "no-create-directory",
        "active-only",
        "no-restore",
        "no-workspace-tools",
        "no-register",
        "wait",
        "loop",
        "enable-repository-automations",
        "disable-repository-automations",
        "pin",
        "unpin",
        "clear-tags",
        "clear-notes",
        "append",
        "absent",
        "contains",
        "case-sensitive",
        "continue-after-failure",
        "stop-on-failure",
        "access-token",
        "otp",
        "device",
        "no-browser",
        "no-serve",
        "local",
        "code",
        "qr",
        "public",
        "video",
        "include-native-logs",
        "include-archived",
        "include-revoked",
        "fail-on-detection",
        "fail-on-change",
        "ignore-whitespace",
        "open",
        "android",
        "ios",
        "offline",
        "all",
        "emulator",
        "emulators",
        "simulator",
        "simulators",
        "physical",
        "physical-devices",
        "virtual",
        "has-logs",
        "has-telemetry",
        "plan-only",
        "dry-run",
        "published",
        "executable-only",
        "include-unavailable",
        "once",
        "runner",
        "skip-build-preflight",
        "upload-recordings",
        "no-upload-recordings",
        "upload-trends",
        "no-upload-trends",
        "upload-test-results",
        "no-upload-test-results"
    };

    private readonly Dictionary<string, List<string?>> options;
    private readonly IReadOnlyList<int> positionalArgumentIndices;

    private CliArguments(
        IReadOnlyList<string> originalArguments,
        IReadOnlyList<string> positionals,
        IReadOnlyList<int> positionalArgumentIndices,
        Dictionary<string, List<string?>> options)
    {
        OriginalArguments = originalArguments;
        Positionals = positionals;
        this.positionalArgumentIndices = positionalArgumentIndices;
        this.options = options;
    }

    public IReadOnlyList<string> Positionals { get; }

    public IReadOnlyList<string> OriginalArguments { get; }

    public bool IsJson => HasFlag("json");

    public bool IsSilent => HasFlag("silent");

    public bool IsVerbose => HasFlag("verbose");

    public bool ShowHelp => HasFlag("help");

    public static CliArguments Parse(IReadOnlyList<string> arguments)
    {
        var positionals = new List<string>();
        var positionalArgumentIndices = new List<int>();
        var options = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-h" or "-?")
            {
                AddOption(options, "help", null);
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                positionalArgumentIndices.Add(index);
                continue;
            }

            var optionText = argument[2..];
            var separatorIndex = optionText.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                AddOption(
                    options,
                    NormalizeOptionName(optionText[..separatorIndex]),
                    optionText[(separatorIndex + 1)..]);
                continue;
            }

            var optionName = NormalizeOptionName(optionText);
            if (IsBooleanOption(optionName, arguments))
            {
                AddOption(options, optionName, null);
                continue;
            }

            if (index + 1 >= arguments.Count
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                AddOption(options, optionName, null);
                continue;
            }

            AddOption(options, optionName, arguments[++index]);
        }

        return new CliArguments(arguments.ToArray(), positionals, positionalArgumentIndices, options);
    }

    private static bool IsBooleanOption(string optionName, IReadOnlyList<string> arguments)
        => BooleanOptions.Contains(optionName)
           && !(string.Equals(optionName, "device", StringComparison.OrdinalIgnoreCase)
                && IsDeviceValueOptionCommand(arguments));

    private static bool IsDeviceValueOptionCommand(IReadOnlyList<string> arguments)
        => IsWorkspaceTestRunCommand(arguments)
           || IsAppExecutionCommand(arguments)
           || IsKeyboardCommand(arguments)
           || arguments.Count >= 1
           && string.Equals(arguments[0], "replay", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyboardCommand(IReadOnlyList<string> arguments)
        => arguments.Count >= 1
           && string.Equals(arguments[0], "keyboard", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppExecutionCommand(IReadOnlyList<string> arguments)
        => arguments.Count >= 2
           && string.Equals(arguments[0], "app", StringComparison.OrdinalIgnoreCase)
           && string.Equals(arguments[1], "execute", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkspaceTestRunCommand(IReadOnlyList<string> arguments)
        => arguments.Count >= 2
           && (string.Equals(arguments[0], "test", StringComparison.OrdinalIgnoreCase)
               || string.Equals(arguments[0], "tests", StringComparison.OrdinalIgnoreCase))
           && (string.Equals(arguments[1], "run", StringComparison.OrdinalIgnoreCase)
               || string.Equals(arguments[1], "run-inline", StringComparison.OrdinalIgnoreCase)
               || string.Equals(arguments[1], "run-all", StringComparison.OrdinalIgnoreCase));

    public bool HasFlag(string name)
        => options.ContainsKey(NormalizeOptionName(name));

    public string? GetOption(string name)
    {
        return options.TryGetValue(NormalizeOptionName(name), out var values)
            ? values.LastOrDefault(static value => value is not null)
            : null;
    }

    public IReadOnlyList<string> GetOptions(string name)
    {
        return options.TryGetValue(NormalizeOptionName(name), out var values)
            ? values.Where(static value => value is not null).Cast<string>().ToArray()
            : [];
    }

    public string RequireOption(string name)
    {
        var normalizedName = NormalizeOptionName(name);
        return GetOption(normalizedName)
               ?? throw new CliUsageException(
                   CliRequirementGuidance.Append(
                       $"--{normalizedName} requires a value.",
                       $"--{normalizedName}"));
    }

    public int GetIntOption(string name, int defaultValue, int minimum, int maximum)
    {
        var source = GetOption(name);
        if (source is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new CliUsageException(
                $"--{NormalizeOptionName(name)} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    public int GetRequiredIntOption(string name, int minimum, int maximum)
    {
        var source = RequireOption(name);
        if (!int.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new CliUsageException(
                $"--{NormalizeOptionName(name)} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    public double GetDoubleOption(string name)
    {
        var source = RequireOption(name);
        if (!double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new CliUsageException(
                $"--{NormalizeOptionName(name)} must be a finite number.");
        }

        return value;
    }

    public TimeSpan GetSecondsOption(string name, TimeSpan defaultValue, int maximumSeconds = 86_400)
    {
        var source = GetOption(name);
        if (source is null)
        {
            return defaultValue;
        }

        if (!double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds < 0
            || seconds > maximumSeconds)
        {
            throw new CliUsageException(
                $"--{NormalizeOptionName(name)} must be a number of seconds between 0 and {maximumSeconds}.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public string RequirePositional(int index, string label)
    {
        if (index >= Positionals.Count || string.IsNullOrWhiteSpace(Positionals[index]))
        {
            throw new CliUsageException(
                CliRequirementGuidance.Append(
                    $"Missing required {label}.",
                    label));
        }

        return Positionals[index].Trim();
    }

    internal int GetOriginalArgumentIndexForPositional(int index)
    {
        if (index < 0 || index >= positionalArgumentIndices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return positionalArgumentIndices[index];
    }

    public void EnsurePositionalCount(int expectedCount, string usage)
    {
        if (Positionals.Count != expectedCount)
        {
            var message = Positionals.Count < expectedCount
                ? $"Missing required arguments. Usage: {usage}"
                : $"Unexpected positional argument. Usage: {usage}";
            throw new CliUsageException(
                Positionals.Count < expectedCount
                    ? CliRequirementGuidance.Append(message, usage)
                    : message);
        }
    }

    public void EnsureOptionContract(
        string commandName,
        IReadOnlyList<string> flagOptions,
        IReadOnlyList<string> valueOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(flagOptions);
        ArgumentNullException.ThrowIfNull(valueOptions);

        var normalizedFlags = flagOptions
            .Select(NormalizeOptionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedValues = valueOptions
            .Select(NormalizeOptionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedOptions = normalizedFlags
            .Concat(normalizedValues)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownOptions = options.Keys
            .Where(option => !allowedOptions.Contains(option))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownOptions.Length > 0)
        {
            var display = string.Join(", ", unknownOptions.Select(static option => $"--{option}"));
            var message = unknownOptions.Length == 1
                ? $"Unknown option '{display}' for '{commandName}'."
                : $"Unknown options {display} for '{commandName}'.";
            var suggestion = unknownOptions.Length == 1
                ? allowedOptions
                    .Where(option => option.StartsWith(unknownOptions[0], StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            throw new CliUsageException(
                suggestion is null
                    ? message
                    : $"{message} Did you mean '--{suggestion}'?");
        }

        foreach (var flagOption in normalizedFlags)
        {
            if (options.TryGetValue(flagOption, out var values)
                && values.Any(static value => value is not null))
            {
                throw new CliUsageException($"--{flagOption} does not accept a value.");
            }
        }

        foreach (var valueOption in normalizedValues)
        {
            if (options.TryGetValue(valueOption, out var values)
                && values.Any(static value => string.IsNullOrWhiteSpace(value)))
            {
                throw new CliUsageException($"--{valueOption} requires a value.");
            }
        }
    }

    private static void AddOption(
        IDictionary<string, List<string?>> options,
        string name,
        string? value)
    {
        if (!options.TryGetValue(name, out var values))
        {
            values = [];
            options.Add(name, values);
        }

        values.Add(value);
    }

    private static string NormalizeOptionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CliUsageException("Option names cannot be empty.");
        }

        return name.Trim().TrimStart('-');
    }
}
