namespace Ansight.Host.Runtime.State;

internal interface IRuntimeState :
    IRuntimeNotifications,
    ISessionReader,
    ISessionLifecycle,
    ISessionIngestion,
    ISessionEditor,
    ISessionAnnotationMutator
{
    void ConfigureHostMetadataCollector(ISessionHostMetadataCollector collector)
    {
    }
}
