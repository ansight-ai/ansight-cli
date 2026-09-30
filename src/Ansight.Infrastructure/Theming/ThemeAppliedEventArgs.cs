namespace Ansight.Infrastructure.Theming;

public sealed class ThemeAppliedEventArgs : EventArgs
{
    public ThemeAppliedEventArgs(ITheme theme)
    {
        Theme = theme;
    }

    public ITheme Theme { get; }
}
