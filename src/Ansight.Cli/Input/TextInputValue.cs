namespace Ansight.Cli.Input;

internal sealed record TextInputValue(string? Value, string? SecretAlias, string? AppId = null)
{
    private const string SecretPrefix = "@secret:";

    public static TextInputValue Parse(CliArguments arguments)
    {
        var values = arguments.GetOptions("value");
        var aliases = arguments.GetOptions("secret");
        if (values.Count + aliases.Count != 1)
        {
            throw new CliUsageException("Provide exactly one --value <text> or --secret <alias>.");
        }

        if (aliases.Count == 1) return FromAlias(aliases[0]);
        var value = values[0];
        if (value.StartsWith("@" + SecretPrefix, StringComparison.Ordinal))
            return new TextInputValue(value[1..], null);
        return value.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? FromReference(value[SecretPrefix.Length..])
            : new TextInputValue(value, null);
    }

    private static TextInputValue FromReference(string reference)
    {
        var separator = reference.IndexOf('/');
        if (separator < 0) return FromAlias(reference);
        var appId = reference[..separator];
        if (appId.Length is < 1 or > 255
            || appId.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
            throw new CliUsageException("A qualified secret reference must use @secret:<app-id>/<alias> with a valid app identifier.");
        return FromAlias(reference[(separator + 1)..]) with { AppId = appId };
    }

    private static TextInputValue FromAlias(string alias)
    {
        if (alias.Length is < 1 or > 128
            || !char.IsAsciiLetterOrDigit(alias[0])
            || alias.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
        {
            throw new CliUsageException("Secret aliases must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).");
        }
        return new TextInputValue(null, alias);
    }
}
