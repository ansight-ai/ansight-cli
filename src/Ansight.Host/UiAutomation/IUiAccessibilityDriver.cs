namespace Ansight.Host.UiAutomation;

public interface IUiAccessibilityDriver
{
    Task<UiAccessibilityResult> CaptureAccessibilityAsync(
        UiAccessibilityRequest request,
        CancellationToken cancellationToken = default);
}
