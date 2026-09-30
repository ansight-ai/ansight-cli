namespace Ansight.Cli.Commands.Secret;

internal static class SecretInput
{
    public static async Task<string> ReadAsync(
        CliArguments arguments,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (CliCommandContext.Current?.SecretValue is { Length: > 0 } forwardedValue)
        {
            return forwardedValue;
        }

        var environmentVariable = arguments.GetOption("from-env");
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrEmpty(value))
            {
                throw new CliUsageException(
                    $"Environment variable '{environmentVariable}' is empty or unavailable.");
            }

            return value;
        }

        if (arguments.HasFlag("stdin"))
        {
            var value = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            value = value.TrimEnd('\r', '\n');
            if (value.Length == 0)
            {
                throw new CliUsageException("No secret value was supplied on stdin.");
            }

            return value;
        }

        if (Console.IsInputRedirected)
        {
            throw new CliUsageException(
                "Secret input is non-interactive. Use --stdin or --from-env <NAME>.");
        }

        Console.Error.Write(prompt);
        var characters = new List<char>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }

        if (characters.Count == 0)
        {
            throw new CliUsageException("The secret value cannot be empty.");
        }

        return new string(characters.ToArray());
    }
}
