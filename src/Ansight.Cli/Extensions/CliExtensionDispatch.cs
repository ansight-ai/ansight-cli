using Ansight.Infrastructure.Extensions;

namespace Ansight.Cli.Extensions;

internal interface ICliExtensionInvoker
{
    object? Invoke(string operation, object?[] arguments);
}

internal static class CliExtensionDispatch
{
    private static readonly OptionalExtensions extensions = new(new object());

    internal static T Invoke<T>(string operation, object?[] arguments)
        => (T)extensions.GetService<ICliExtensionInvoker>("Ansight.Cloud.Cli").Invoke(operation, arguments)!;
}
