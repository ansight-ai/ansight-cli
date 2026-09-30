namespace Ansight.Infrastructure.Utilities;

/// <summary>
/// A helper class for working with attributes on <see cref="Type"/>.
/// </summary>
public static class AttributeHelper
{
    /// <summary>
    /// Does the provided <paramref name="type"/> have a <typeparamref name="TAttribute"/> attribute?
    /// </summary>
    public static bool HasAttribute<TAttribute>(Type type, bool inherit = true) where TAttribute : Attribute
    {
        var matches = type.GetCustomAttributes(typeof(TAttribute), inherit);

        return matches.Any();
    }

    /// <summary>
    /// Gets the <typeparamref name="TAttribute"/> attribute on <paramref name="value"/>.
    /// </summary>
    public static TAttribute? GetAttribute<TAttribute>(Enum value, bool inherit = true) where TAttribute : Attribute
    {
        var enumType = value.GetType();
        var name = Enum.GetName(enumType, value);
        var field = name is null ? null : enumType.GetField(name);
        return field?.GetCustomAttributes(inherit).OfType<TAttribute>().FirstOrDefault();
    }

    /// <summary>
    /// Gets the first <typeparamref name="TAttribute"/> attribute on <paramref name="type"/>.
    /// </summary>
    /// <returns>The attribute.</returns>
    public static TAttribute? GetAttribute<TAttribute>(Type type, bool inherit = true) where TAttribute : Attribute
    {
        var matches = type.GetCustomAttributes(typeof(TAttribute), inherit);

        return matches.Cast<TAttribute>().FirstOrDefault();
    }

    /// <summary>
    /// Gets the <typeparamref name="TAttribute"/> attributes on <paramref name="type"/>.
    /// </summary>
    public static IEnumerable<TAttribute> GetAttributes<TAttribute>(Type type, bool inherit = true) where TAttribute : Attribute
    {
        var matches = type.GetCustomAttributes(typeof(TAttribute), inherit);

        return matches.Cast<TAttribute>().ToList();
    }

    /// <summary>
    /// Gets the <typeparamref name="TAttribute"/> attributes on <paramref name="value"/>.
    /// </summary>
    public static IReadOnlyList<TAttribute> GetAttributes<TAttribute>(Enum value, bool inherit = true) where TAttribute : Attribute
    {
        var enumType = value.GetType();
        var name = Enum.GetName(enumType, value);
        var field = name is null ? null : enumType.GetField(name);
        return field?.GetCustomAttributes(inherit).OfType<TAttribute>().ToList() ?? [];
    }
}
