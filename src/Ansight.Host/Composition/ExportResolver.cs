namespace Ansight.Host.Composition;

using Ansight.Host;
using Ansight.Infrastructure.Preferences;
using Ansight.Infrastructure.Security;

internal sealed class ExportResolver : AssemblyCompositionExportResolver
{
    private readonly IApplicationPaths applicationPaths;
    private readonly IEncryptedStorage encryptedStorage;
    private readonly ISessionVideoEncoder? sessionVideoEncoder;

    public ExportResolver(
        IApplicationPaths applicationPaths,
        IEncryptedStorage encryptedStorage,
        ISessionVideoEncoder? sessionVideoEncoder)
    {
        this.applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
        this.encryptedStorage = encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage));
        this.sessionVideoEncoder = sessionVideoEncoder;
    }

    public override IReadOnlyList<Assembly> Assemblies => [typeof(ExportResolver).Assembly];

    protected override void RegisterExternalParts(IExternalPartRegistrar registrar)
    {
        registrar.RegisterSingleton(applicationPaths);
        registrar.RegisterSingleton(encryptedStorage);
        var hostRuntimeOptions = new RuntimeOptions();
        registrar.RegisterSingleton(hostRuntimeOptions);
        // Keep the established filename so older hosts and rollback share the same settings.
        var preferencesStore = new FilePreferencesStore(Path.Combine(applicationPaths.ApplicationDataPath, "studio-preferences.json"));
        registrar.RegisterSingleton<IPreferencesStore>(preferencesStore);
        registrar.RegisterSingleton<IUserPreferences>(new UserPreferences(preferencesStore));

    }
}
