namespace Ansight.Host.Workspaces.Execution;

internal sealed class DelegatingProgress<T> : IProgress<T>
{
    private readonly Action<T> report;

    public DelegatingProgress(Action<T> report)
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public void Report(T value)
    {
        report(value);
    }
}
