namespace Ansight.SimCtl;

public interface ISimCtlProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    Task<int> Completion { get; }

    void Terminate(bool force = false);
}
