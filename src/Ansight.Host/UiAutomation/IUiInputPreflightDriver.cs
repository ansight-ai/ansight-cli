namespace Ansight.Host.UiAutomation;

public interface IUiInputPreflightDriver
{
    Task<UiInputResult> PrepareForInputAsync(
        UiInputPreflightRequest request,
        CancellationToken cancellationToken = default);
}
