namespace Ansight.Host.Identity;

internal interface IIdentityStore : IDisposable
{
    RuntimeIdentity Current { get; }
}
