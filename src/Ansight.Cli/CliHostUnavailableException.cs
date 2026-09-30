namespace Ansight.Cli;

internal sealed class CliHostUnavailableException : Exception
{
    public CliHostUnavailableException(string message)
        : base(message)
    {
    }
}
