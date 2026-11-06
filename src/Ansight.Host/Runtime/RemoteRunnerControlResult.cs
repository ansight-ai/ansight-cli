namespace Ansight.Host.Runtime;

public sealed record RemoteRunnerControlResult(bool IsSuccess, string Message, RemoteRunnerRegistration? Registration = null);
