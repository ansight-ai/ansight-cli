namespace Ansight.Infrastructure.IOC;

public interface IExportResolver
{
    void Prepare();

    void PostPrepare();

    Lazy<T> GetExport<T>();

    Lazy<IEnumerable<T>> GetExports<T>();

    T GetExportedValue<T>();

    IEnumerable<T> GetExportedValues<T>();

    object? GetExportedValue(Type type);

    IEnumerable<object> GetExportedValues(Type type);

    void ComposeParts(object instance);
}
