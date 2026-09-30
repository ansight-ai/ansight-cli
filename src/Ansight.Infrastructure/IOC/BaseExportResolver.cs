namespace Ansight.Infrastructure.IOC;

public abstract class BaseExportResolver : IExportResolver
{
    public virtual void Prepare()
    {
    }

    public virtual void PostPrepare()
    {
    }

    public abstract Lazy<T> GetExport<T>();

    public abstract Lazy<IEnumerable<T>> GetExports<T>();

    public abstract T GetExportedValue<T>();

    public abstract IEnumerable<T> GetExportedValues<T>();

    public abstract object? GetExportedValue(Type type);

    public abstract IEnumerable<object> GetExportedValues(Type type);

    public abstract void ComposeParts(object instance);
}
