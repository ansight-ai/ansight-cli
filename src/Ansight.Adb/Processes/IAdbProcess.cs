namespace Ansight.Adb;

public interface IAdbProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    Task<int> Completion { get; }

    void Terminate(bool force = false);
}
