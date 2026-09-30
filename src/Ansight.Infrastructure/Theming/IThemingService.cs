namespace Ansight.Infrastructure.Theming;

public interface IThemingService
{
    IReadOnlyList<ITheme> Themes { get; }

    ITheme CurrentTheme { get; }

    ITheme DefaultTheme { get; }

    ITheme? GetTheme(string identifier);

    ITheme? GetTheme(ThemeKind themeKind);
}
