using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly TestDirectory directory = TestDirectory.Create();

    public string RootPath => directory.Path;

    public void Dispose() => directory.Dispose();
}
