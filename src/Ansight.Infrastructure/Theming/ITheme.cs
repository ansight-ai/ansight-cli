namespace Ansight.Infrastructure.Theming;

public interface ITheme : IColorSet
{
    string Identifier { get; }

    string Name { get; }

    ThemeKind ThemeKind { get; }

    IReadOnlyDictionary<string, Color> Values { get; }
}
