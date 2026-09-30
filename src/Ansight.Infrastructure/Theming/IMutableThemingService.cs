namespace Ansight.Infrastructure.Theming;

public interface IMutableThemingService : IThemingService
{
    event EventHandler<ThemeApplyingEventArgs>? ThemeApplying;

    event EventHandler<ThemeAppliedEventArgs>? ThemeApplied;

    void Startup();

    void ApplyTheme(ITheme theme, string triggeredBy);
}
