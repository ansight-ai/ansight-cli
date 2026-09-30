namespace Ansight.Infrastructure.Logging;

public class SuspendLogFormattingScope : ISuspendLogFormattingScope
{
    public void Dispose()
    {
        IsSuspended = false;
        // TODO release managed resources here
    }

    public bool IsSuspended { get; private set; } = true;
}
