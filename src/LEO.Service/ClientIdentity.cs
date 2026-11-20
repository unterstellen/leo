using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Leo.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Leo.Service;

/// <summary>
/// Who is on the other end of a control-pipe connection, and what they may do (LEO.md §4.2):
/// administrators (elevated or split-token) and SYSTEM get <see cref="ClientAccess.Full"/>,
/// standard users get <see cref="ClientAccess.ReadOnly"/>.
/// </summary>
/// <remarks>
/// Deliberately does NOT impersonate the client (NamedPipeServerStream.RunAsClient). An identification-level
/// impersonation token left on a thread-pool thread makes later lazy assembly loads fail with
/// FileNotFoundException (0x80070002) — which silently killed StreamJsonRpc's read loop. Instead we read the
/// token of the client *process* (pid from GetNamedPipeClientProcessId), which SYSTEM may query.
/// </remarks>
public sealed record ClientIdentity(int ProcessId, string ImagePath, string UserName, ClientAccess Access)
{
    public static ClientIdentity FromPipe(NamedPipeServerStream pipe)
    {
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var rawPid))
        {
            return new ClientIdentity(0, "?", "?", ClientAccess.ReadOnly);
        }

        var pid = (int)rawPid;
        using var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, rawPid);
        if (process.IsInvalid)
        {
            return new ClientIdentity(pid, "?", "?", ClientAccess.ReadOnly);
        }

        var image = TryGetImagePath(process) ?? "?";
        var user = "?";
        var access = ClientAccess.ReadOnly;

        // TOKEN_DUPLICATE as well: WindowsPrincipal.IsInRole duplicates the token internally.
        if (Native.OpenProcessToken(process, Native.TokenQuery | Native.TokenDuplicate, out var token))
        {
            using (token)
            {
                try
                {
                    using var identity = new WindowsIdentity(token.DangerousGetHandle());
                    user = identity.Name;
                    access = IsAdministrator(identity, token) ? ClientAccess.Full : ClientAccess.ReadOnly;
                }
                catch
                {
                    // stay read-only
                }
            }
        }

        return new ClientIdentity(pid, image, user, access);
    }

    private static bool IsAdministrator(WindowsIdentity id, SafeAccessTokenHandle token)
    {
        if (id.IsSystem) return true;

        // A non-elevated admin has a filtered ("limited") token whose Administrators SID is deny-only.
        // TokenElevationTypeLimited means the user *is* an administrator (UAC split token).
        if (Native.GetTokenInformation(token, Native.TokenElevationTypeClass, out var type, sizeof(int), out _) &&
            type == Native.TokenElevationTypeLimited)
        {
            return true;
        }

        // Elevated admins, or admins with UAC disabled.
        try { return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private static string? TryGetImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint TokenQuery = 0x0008;
        public const uint TokenDuplicate = 0x0002;
        public const int TokenElevationTypeClass = 18;
        public const int TokenElevationTypeLimited = 3;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int cls,
            out int information, int length, out int returnLength);
    }
}
