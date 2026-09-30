namespace Ansight.Host.Runtime.State;

internal interface IRuntimeState :
    IRuntimeNotifications,
    ISessionReader,
    ISessionLifecycle,
    ISessionIngestion,
    ISessionEditor,
    ISessionAnnotationMutator
{
}
