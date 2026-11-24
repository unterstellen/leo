using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Leo.Core;
using Microsoft.Win32.SafeHandles;

namespace Leo.Ipc;

/// <summary>
/// Who serves the pipe the UI just connected to. A standard (non-elevated) user usually can't read a SYSTEM service's
/// image path, so the Service Control Manager's view is used as well: the PID it reports for the service "LEO" and
/// the service's registered exe (found live in M8).
/// </summary>
public sealed record PipeServerInfo(uint SessionId, string? ImagePath, uint ProcessId = 0,
    uint? ServiceProcessId = null, string? ServiceImagePath = null);

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
        if (!string.IsNullOrEmpty(server.ImagePath)) return ExeProblem(server.ImagePath, installDir, "the pipe server");

        // Image not readable (normal for a standard user): the server must be the process Windows runs as service "LEO".
        if (server.ServiceProcessId is not { } servicePid || servicePid == 0) return "LEO's service isn't running";
        if (server.ProcessId != servicePid) return "the pipe server isn't LEO's service process";
        if (string.IsNullOrEmpty(server.ServiceImagePath)) return "LEO's service registration couldn't be read";
        return ExeProblem(server.ServiceImagePath, installDir, "LEO's service");
    }

    private static string? ExeProblem(string imagePath, string installDir, string what)
    {
        if (!string.Equals(Path.GetFileName(imagePath), ServiceExeName, StringComparison.OrdinalIgnoreCase))
            return $"{what} is {Path.GetFileName(imagePath)}, not {ServiceExeName}";
        if (!LeoPaths.IsInside(imagePath, installDir)) return $"{what} isn't in LEO's install folder";
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
            var havePid = GetNamedPipeServerProcessId(handle, out var pid);
            if (havePid)
            {
                using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (!process.IsInvalid)
                {
                    var buffer = new StringBuilder(1024);
                    var size = buffer.Capacity;
                    if (QueryFullProcessImageName(process, 0, buffer, ref size)) image = buffer.ToString();
                }
            }
            var (servicePid, serviceImage) = ServiceView(LeoInfo.ServiceName);
            return new PipeServerInfo(session, image, havePid ? pid : 0, servicePid, serviceImage);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The SCM's process id for a service (any user may query LEO's status) and its registered exe.</summary>
    public static (uint? Pid, string? Image) ServiceView(string serviceName)
    {
        uint? pid = null;
        var scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm != IntPtr.Zero)
        {
            try
            {
                var svc = OpenService(scm, serviceName, 0x0004 /* SERVICE_QUERY_STATUS */);
                if (svc != IntPtr.Zero)
                {
                    try
                    {
                        var buffer = new byte[36]; // SERVICE_STATUS_PROCESS
                        if (QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, buffer, buffer.Length, out _))
                            pid = BitConverter.ToUInt32(buffer, 28);
                    }
                    finally { CloseServiceHandle(svc); }
                }
            }
            finally { CloseServiceHandle(scm); }
        }

        string? image = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("ImagePath") is string raw) image = ExePath(raw);
        }
        catch { /* not readable */ }
        return (pid, image);
    }

    /// <summary>The exe from a service ImagePath (quoted exe plus arguments, or unquoted).</summary>
    public static string ExePath(string imagePath)
    {
        var s = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? s[..(i + 4)] : s;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, byte[] buffer, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

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
