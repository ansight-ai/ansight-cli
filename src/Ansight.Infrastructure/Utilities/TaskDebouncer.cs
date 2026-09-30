namespace Ansight.Infrastructure.Utilities;

public class TaskDebouncer
{
    private readonly Action<string, Exception> logError;
    private readonly Func<Task> onDebounce;
    private readonly int debounceMilliseconds;
    private CancellationTokenSource? searchThrottleCancellationToken;

    public TaskDebouncer(Action<string, Exception> logError,
        Func<Task> onDebounce,
        int debounceMilliseconds = 60)
    {
        this.logError = logError ?? throw new ArgumentNullException(nameof(logError));
        this.onDebounce = onDebounce ?? throw new ArgumentNullException(nameof(onDebounce));
        this.debounceMilliseconds = debounceMilliseconds;
    }

    public async Task Debounce()
    {
        try
        {
            var currentToken = new CancellationTokenSource();
            var oldToken = Interlocked.Exchange(ref searchThrottleCancellationToken, currentToken);

            oldToken?.Cancel();
            oldToken?.Dispose();

            await Task.Delay(TimeSpan.FromMilliseconds(debounceMilliseconds), currentToken.Token)
                .ContinueWith(async task => await onDebounce(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.FromCurrentSynchronizationContext());
        }
        catch (TaskCanceledException)
        {
            // ignore this expected exception
        }
        catch (OperationCanceledException)
        {
            // ignore this expected exception
        }
        catch (Exception ex)
        {
            logError("Error in debounce event.", ex);
        }
    }
}
