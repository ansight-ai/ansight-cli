namespace Ansight.Cli;

internal sealed class CliProgress<T> : IProgress<T>
{
    private readonly Action<T> report;

    public CliProgress(Action<T> report)
    {
        this.report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public void Report(T value)
    {
        report(value);
    }
}
