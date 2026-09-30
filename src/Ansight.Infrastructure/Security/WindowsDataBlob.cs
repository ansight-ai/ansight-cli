using System.Runtime.InteropServices;

namespace Ansight.Infrastructure.Security;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsDataBlob
{
    public int Size;

    public nint Data;
}
