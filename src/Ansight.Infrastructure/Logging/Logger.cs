using System.Runtime.CompilerServices;

namespace Ansight.Infrastructure.Logging;

public sealed class Logger
{
    public static readonly Logger Instance = new Logger();

    private readonly object factoryLock = new();

    private ILoggerFactory? factory = new ConsoleLoggerFactory();

    public ILoggerFactory Factory
    {
        get
        {
            lock (factoryLock)
            {
                return factory ?? throw new ObjectDisposedException(nameof(Logger));
            }
        }
        set
        {
            lock (factoryLock)
            {
                factory = value ?? throw new ArgumentNullException(nameof(value));
            }
        }
    }

    /// <summary>
    /// Creates a new <see cref="ILogger"/>, capturing the name of the file where is was created as the logger context.
    /// </summary>
    public static ILogger Create([CallerFilePath]string tag = "", bool removeFileExtension = true)
    {
        if (tag is null)
        {
            throw new ArgumentNullException(nameof(tag));
        }

        if (removeFileExtension)
        {
            tag = Path.GetFileNameWithoutExtension(tag);
        }

        return Instance.Factory.Create(tag);
    }

    public void Close()
    {
        lock (factoryLock)
        {
            factory?.Dispose();
            factory = null;
        }
    }
}
