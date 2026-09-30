namespace Ansight.Host.Pairing;

internal interface IPairingConfigService
{
    PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration);

    PairingTransientIssueResult IssueTransient(
        string appName,
        string appId,
        PairingConfigDuration duration)
    {
        var result = Issue(appName, appId, duration);
        return new PairingTransientIssueResult(result.ConfigId, result.ExpiresAt);
    }
}
