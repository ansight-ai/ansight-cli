namespace Ansight.Host.UiAutomation;

public interface IUiInputDriver
{
    UiInputAvailability GetAvailability(string deviceIdentifier);

    Task<UiInputResult> TapAsync(
        UiTapRequest request,
        CancellationToken cancellationToken = default);

    Task<UiInputResult> SwipeAsync(
        UiSwipeRequest request,
        CancellationToken cancellationToken = default);

    Task<UiInputResult> PinchAsync(
        UiPinchRequest request,
        CancellationToken cancellationToken = default);

    Task<UiInputResult> TypeTextAsync(
        UiTextRequest request,
        CancellationToken cancellationToken = default);

    Task<UiInputResult> PressButtonAsync(
        UiButtonRequest request,
        CancellationToken cancellationToken = default);
}

public interface IUiInputPreflightDriver
{
    Task<UiInputResult> PrepareForInputAsync(
        UiInputPreflightRequest request,
        CancellationToken cancellationToken = default);
}
