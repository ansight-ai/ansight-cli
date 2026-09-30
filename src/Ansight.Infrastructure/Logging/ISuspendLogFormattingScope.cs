namespace Ansight.Infrastructure.Logging;

public interface ISuspendLogFormattingScope : IDisposable
{
    bool IsSuspended { get; }

}
