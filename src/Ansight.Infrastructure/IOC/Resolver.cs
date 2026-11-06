namespace Ansight.Infrastructure.IOC;

public static class Resolver
{
    private static readonly object gate = new();
    private static IExportResolver? resolver;

    public static void Initialise(IExportResolver resolver)
    {
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        lock (gate)
        {
            if (Resolver.resolver is not null)
            {
                throw new InvalidOperationException("Resolver is already initialised.");
            }

            resolver.Prepare();
            resolver.PostPrepare();
            Resolver.resolver = resolver;
        }
    }

    public static T Resolve<T>() where T : class
    {
        lock (gate)
        {
            if (resolver is null)
            {
                throw new InvalidOperationException("Resolver has not been initialised.");
            }

            return resolver.GetExportedValue<T>();
        }
    }

    public static object? Resolve(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        lock (gate)
        {
            if (resolver is null)
            {
                throw new InvalidOperationException("Resolver has not been initialised.");
            }

            return resolver.GetExportedValue(type);
        }
    }

    public static void ComposeParts(object instance)
    {
        lock (gate)
        {
            if (resolver is null)
            {
                throw new InvalidOperationException("Resolver has not been initialised.");
            }

            resolver.ComposeParts(instance);
        }
    }

    public static void ResetForTests()
    {
        lock (gate)
        {
            resolver = null;
        }
    }
}
