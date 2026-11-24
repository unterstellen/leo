using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Leo.Core;
using Microsoft.Win32.SafeHandles;

namespace Leo.Ipc;

/// <summary>Who serves the pipe the UI just connected to.</summary>
public sealed record PipeServerInfo(uint SessionId, string? ImagePath);

/// <summary>
/// Pipe squatting check on the UI side (LEO.md §24.6 item 3): before trusting <c>\\.\pipe\LEO.Control</c> the UI
/// checks that the server runs in session 0 (a service) and is <c>LEO.Service.exe</c> inside LEO's install folder.
/// Enforced when the UI itself runs from the install folder (an MSI install); dev builds only log a mismatch, so a
/// service started from a terminal keeps working.
/// </summary>
public static class PipeServerCheck
{
    public const string ServiceExeName = "LEO.Service.exe";

    /// <summary>Null when the server is acceptable, else the reason (for the UI log).</summary>
    public static string? Problem(PipeServerInfo? server, string installDir)
    {
        if (server is null) return "the pipe server couldn't be identified";
        if (server.SessionId != 0) return $"the pipe server runs in session {server.SessionId}, not as a service";
        if (string.IsNullOrEmpty(server.ImagePath)) return "the pipe server's program couldn't be read";
        if (!string.Equals(Path.GetFileName(server.ImagePath), ServiceExeName, StringComparison.OrdinalIgnoreCase))
            return $"the pipe server is {Path.GetFileName(server.ImagePath)}, not {ServiceExeName}";
        if (!LeoPaths.IsInside(server.ImagePath, installDir)) return "the pipe server isn't in LEO's install folder";
        return null;
    }

    /// <summary>The check is enforced when this UI runs from LEO's install folder.</summary>
    public static bool ShouldEnforce(string? ownExe, string installDir) => ownExe is not null && LeoPaths.IsInside(ownExe, installDir);

    public static PipeServerInfo? Identify(NamedPipeClientStream pipe)
    {
        try
        {
            var handle = pipe.SafePipeHandle;
            if (!GetNamedPipeServerSessionId(handle, out var session)) return null;
            string? image = null;
            if (GetNamedPipeServerProcessId(handle, out var pid))
            {
                using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (!process.IsInvalid)
                {
                    var buffer = new StringBuilder(1024);
                    var size = buffer.Capacity;
                    if (QueryFullProcessImageName(process, 0, buffer, ref size)) image = buffer.ToString();
                }
            }
            return new PipeServerInfo(session, image);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
