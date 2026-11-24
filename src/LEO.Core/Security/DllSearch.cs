using System.Runtime.InteropServices;

namespace Leo.Core.Security;

/// <summary>
/// Both LEO processes load DLLs only from System32 and their own (admin-only writable) install folder, never from the
/// current directory or PATH (LEO.md §24.6 item 6). Called first thing at start.
/// </summary>
public static class DllSearch
{
    private const uint LoadLibrarySearchApplicationDir = 0x00000200;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    public static bool Restrict()
    {
        try
        {
            return SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchApplicationDir);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
