namespace Ansight.Cli.Auth;

internal sealed record HostAccessState(AccessDecision Decision, Uri? ExplorerUrl = null, CliAccessLease? Lease = null);
