using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Reflection;

namespace Ansight.Infrastructure.IOC;

public abstract class AssemblyCompositionExportResolver : BaseExportResolver
{
    private ExternalPartComponentCatalog? externalParts;

    protected CompositionContainer? CompositionRoot { get; private set; }

    public AggregateCatalog? Catalog { get; private set; }

    public override void Prepare()
    {
        if (CompositionRoot is not null)
        {
            throw new InvalidOperationException($"The {nameof(AssemblyCompositionExportResolver)} is already prepared.");
        }

        Catalog = new AggregateCatalog();
        foreach (var assembly in Assemblies)
        {
            Catalog.Catalogs.Add(new AssemblyCatalog(assembly));
        }

        externalParts = new ExternalPartComponentCatalog();
        RegisterExternalParts(externalParts);
        Catalog.Catalogs.Add(externalParts);

        CompositionRoot = new CompositionContainer(Catalog);
    }

    public virtual IReadOnlyList<Assembly> Assemblies => GetExportedAppDomainAssemblies();

    protected virtual IReadOnlyList<Assembly> GetExportedAppDomainAssemblies()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetCustomAttributes(typeof(ExportAssemblyAttribute)).Any())
            .ToList();
    }

    protected abstract void RegisterExternalParts(IExternalPartRegistrar registrar);

    public sealed override Lazy<T> GetExport<T>()
    {
        EnsurePrepared();
        return CompositionRoot!.GetExport<T>()!;
    }

    public sealed override T GetExportedValue<T>()
    {
        EnsurePrepared();
        return CompositionRoot!.GetExportedValue<T>()!;
    }

    public sealed override IEnumerable<T> GetExportedValues<T>()
    {
        EnsurePrepared();
        return CompositionRoot!.GetExportedValues<T>();
    }

    public sealed override Lazy<IEnumerable<T>> GetExports<T>()
    {
        return new Lazy<IEnumerable<T>>(() => GetExportedValues<T>());
    }

    public sealed override object? GetExportedValue(Type type)
    {
        if (type is null)
        {
            return null;
        }

        var methodInfo = GetType().GetMethods()
            .First(method => method.Name == nameof(GetExportedValue) && method.GetParameters().Length == 0)
            .MakeGenericMethod(type);

        return methodInfo.Invoke(this, null);
    }

    public sealed override IEnumerable<object> GetExportedValues(Type type)
    {
        var methodInfo = GetType().GetMethods()
            .First(method => method.Name == nameof(GetExportedValues) && method.GetParameters().Length == 0)
            .MakeGenericMethod(type);

        return (IEnumerable<object>)(methodInfo.Invoke(this, null) ?? Enumerable.Empty<object>());
    }

    public sealed override void ComposeParts(object instance)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        EnsurePrepared();
        AttributedModelServices.SatisfyImportsOnce(CompositionRoot!, instance);
    }

    private void EnsurePrepared()
    {
        if (CompositionRoot is null)
        {
            throw new InvalidOperationException($"{nameof(Prepare)} must be called before resolving exports.");
        }
    }
}
