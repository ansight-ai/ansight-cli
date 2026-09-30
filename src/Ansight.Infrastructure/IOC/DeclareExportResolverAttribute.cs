namespace Ansight.Infrastructure.IOC;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public class DeclareExportResolverAttribute : Attribute
{
    public DeclareExportResolverAttribute(Type exportResolverType)
    {
        ExportResolverType = exportResolverType;
    }

    public Type ExportResolverType { get; }
}
