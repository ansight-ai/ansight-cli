namespace Ansight.Host.Explorer;

internal interface IExplorerExtension
{
    object Invoke(object server, string operation, object?[] arguments);
}
