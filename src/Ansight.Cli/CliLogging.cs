using Ansight.Infrastructure.Logging;

namespace Ansight.Cli;

internal static class CliLogging
{
    public static IDisposable Configure(
        CliArguments arguments,
        bool verbose,
        bool silent = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ILoggerFactory factory = IsHostRun(arguments)
            ? TryCreateHostLoggerFactory(arguments, verbose, silent)
            : CreateConsoleLoggerFactory(verbose, silent);
        Ansight.Infrastructure.Logging.Logger.Instance.Factory = factory;
        if (factory is IApplicationLoggerFactory)
        {
            var identity = CliReleaseIdentity.Current;
            Ansight.Infrastructure.Logging.Logger.Create().Info(
                $"host_process_starting version={identity.Version} buildNumber={identity.BuildNumber} "
                + $"commitSha={identity.CommitSha} executable={Environment.ProcessPath ?? "unavailable"}");
        }

        return factory;
    }

    internal static string? CurrentLogFilePath
        => Ansight.Infrastructure.Logging.Logger.Instance.Factory is IApplicationLoggerFactory factory
           && !string.IsNullOrWhiteSpace(factory.LogFilePath)
            ? factory.LogFilePath
            : null;

    private static ILoggerFactory TryCreateHostLoggerFactory(
        CliArguments arguments,
        bool verbose,
        bool silent)
    {
        try
        {
            var dataDirectory = CliRuntime.ResolveDataDirectory(arguments.GetOption("data-dir"));
            return new CliHostLoggerFactory(
                Path.Combine(dataDirectory, LoggingConstants.LogsFolderName),
                verbose,
                silent);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or InvalidOperationException
                                           or UnauthorizedAccessException)
        {
            return CreateConsoleLoggerFactory(verbose, silent);
        }
    }

    private static ConsoleLoggerFactory CreateConsoleLoggerFactory(bool verbose, bool silent)
        => new((tag, message, level) =>
        {
            if (ShouldWrite(level, verbose, silent))
            {
                Console.Error.WriteLine($"[{level}] {tag}: {message}");
            }

            return true;
        });

    private static bool IsHostRun(CliArguments arguments)
    {
        return arguments.Positionals.Count >= 2
            && string.Equals(arguments.Positionals[0], "host", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments.Positionals[1], "run", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldWrite(LogLevel level, bool verbose, bool silent = false)
        => !silent && (verbose || level >= LogLevel.Warning);

    private sealed class CliHostLoggerFactory : ApplicationLoggerFactory
    {
        private readonly bool verbose;
        private readonly bool silent;

        public CliHostLoggerFactory(string applicationLogsPath, bool verbose, bool silent)
            : base(applicationLogsPath, writeToFile: true)
        {
            this.verbose = verbose;
            this.silent = silent;
            SetMinimumLogLevel(LogLevel.Verbose);
        }

        public override ILogger Create(string tag)
            => new CliHostLogger(
                tag,
                LogFilter,
                ProcessId,
                MutableApplicationLogBuffer.Instance,
                LogFileWriter,
                verbose,
                silent);

        protected override string CreateLogFilePath(string logsFolderPath)
            => Path.Combine(
                logsFolderPath,
                $"host-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}-{ProcessId}.log");
    }

    private sealed class CliHostLogger : ApplicationLogger
    {
        private readonly bool verbose;
        private readonly bool silent;

        public CliHostLogger(
            string tag,
            ILogFilter filter,
            int processId,
            IMutableApplicationLogBuffer applicationLogBuffer,
            LogFileWriter? logFileWriter,
            bool verbose,
            bool silent)
            : base(tag, filter, processId, applicationLogBuffer, logFileWriter)
        {
            this.verbose = verbose;
            this.silent = silent;
        }

        protected override bool PlatformLog(string tag, string message, LogLevel logLevel)
        {
            if (ShouldWrite(logLevel, verbose, silent))
            {
                Console.Error.WriteLine($"[{logLevel}] {tag}: {message}");
            }

            return true;
        }
    }
}
