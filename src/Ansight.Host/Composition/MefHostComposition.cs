namespace Ansight.Host.Composition;

using Ansight.Infrastructure.Security;

internal sealed class MefHostComposition
{
    private readonly ExportResolver exportResolver;

    public MefHostComposition(
        IApplicationPaths applicationPaths,
        IEncryptedStorage encryptedStorage,
        ISessionVideoEncoder? sessionVideoEncoder = null)
    {
        ApplicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        exportResolver = new ExportResolver(ApplicationPaths, encryptedStorage, sessionVideoEncoder);
        exportResolver.Prepare();
        exportResolver.PostPrepare();
    }

    public MefHostComposition(IEncryptedStorage encryptedStorage)
        : this(ApplicationPathsFactory.Create(), encryptedStorage)
    {
    }

    public IApplicationPaths ApplicationPaths { get; }

    public T Get<T>() where T : notnull
    {
        return exportResolver.GetExportedValue<T>();
    }
}
