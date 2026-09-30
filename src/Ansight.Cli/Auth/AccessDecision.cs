namespace Ansight.Cli.Auth;

internal sealed record AccessDecision(
    bool IsAuthorized,
    string Reason,
    DateTimeOffset? EvaluatedAt = null,
    DateTimeOffset? AuthorizedUntil = null,
    string? UserId = null)
{
    public static AccessDecision AuthenticationRequired { get; } = new(false, "authentication_required");
    public static AccessDecision MachineRegistrationRequired { get; } = new(false, "machine_registration_required");
    public static AccessDecision ProductAccessRequired { get; } = new(false, "product_access_required");
    public static AccessDecision Unavailable { get; } = new(false, "authorization_unavailable");

    public string Message => Reason switch
    {
        "local" => "Local developer tools are free and do not require an account.",
        "active" => $"Cloud access is authenticated until {AuthorizedUntil:O}.",
        "authentication_required" => "Sign in with 'ansight account login' to use Ansight Cloud. Local developer tools do not require an account.",
        "machine_registration_required" => "This machine registration was removed. Sign in again with "
                                           + "'ansight account login' to register it.",
        "product_access_required" => "This cloud operation requires an active cloud grant. "
                                     + "Run 'ansight account open' to manage cloud access at https://app.ansight.ai/.",
        _ => "Unable to verify Ansight Cloud access. Check your connection and run 'ansight account access'."
    };

    public int WriteFailure(CliOutput output)
    {
        output.WriteError(Reason, Message, CliExitCodes.AccessDenied);
        return CliExitCodes.AccessDenied;
    }
}
