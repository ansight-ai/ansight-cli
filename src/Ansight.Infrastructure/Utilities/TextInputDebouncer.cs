using System;
using System.Threading.Tasks;
using System.Threading;

namespace Ansight.Infrastructure.Utilities;

public class TextInputDebouncer
{
    private readonly Action<string, Exception> logError;
    private readonly Func<string?, Task> onText;
    private readonly int debounceMilliseconds;
    private CancellationTokenSource? searchThrottleCancelllationToken;
    private volatile string? latestSearchText;

    public TextInputDebouncer(Action<string, Exception> logError,
        Func<string?, Task> onText,
        int debounceMilliseconds = 600)
    {
        this.logError = logError ?? throw new ArgumentNullException(nameof(logError));
        this.onText = onText ?? throw new ArgumentNullException(nameof(onText));
        this.debounceMilliseconds = debounceMilliseconds;
    }

    public async Task Debounce(string? text)
    {
        try
        {
            var currentToken = new CancellationTokenSource();
            var oldToken = Interlocked.Exchange(ref searchThrottleCancelllationToken, currentToken);

            oldToken?.Cancel();
            oldToken?.Dispose();

            text = text?.Trim();
            text = string.IsNullOrEmpty(text) ? null : text;
            latestSearchText = text;

            await Task.Delay(TimeSpan.FromMilliseconds(debounceMilliseconds), currentToken.Token)
                .ContinueWith(async task => await Filter(text),
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
            logError("Error in text debounce event.", ex);
        }
    }

    private async Task Filter(string? searchText)
    {
        if (latestSearchText == searchText)
        {
            await onText(searchText);
        }
    }
}
