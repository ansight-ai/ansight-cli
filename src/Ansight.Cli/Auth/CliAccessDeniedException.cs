namespace Ansight.Cli.Auth;

internal sealed class CliAccessDeniedException : Exception
{
    public CliAccessDeniedException(AccessDecision decision) : base(decision.Message)
    {
        this.Decision = decision;
    }

    public AccessDecision Decision { get; }
}
