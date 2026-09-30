namespace Ansight.Infrastructure.Theming;

public sealed class ThemeApplyingEventArgs : EventArgs
{
    public ThemeApplyingEventArgs(ITheme? previousTheme, ITheme newTheme)
    {
        PreviousTheme = previousTheme;
        NewTheme = newTheme;
    }

    public ITheme? PreviousTheme { get; }

    public ITheme NewTheme { get; }
}
