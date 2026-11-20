using System.ComponentModel;
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
public sealed record ClientIdentity(int ProcessId, string ImagePath, string UserName, ClientAccess Access)
{
    public static ClientIdentity FromPipe(NamedPipeServerStream pipe)
    {
        var pid = 0;
        var image = "?";
        var user = "?";
        var access = ClientAccess.ReadOnly;

        try
        {
            if (Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var rawPid))
            {
                pid = (int)rawPid;
                image = TryGetImagePath(pid) ?? "?";
            }
        }
        catch { /* best effort */ }

        try
        {
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                user = id.Name;
                access = IsAdministrator(id) ? ClientAccess.Full : ClientAccess.ReadOnly;
            });
        }
        catch { /* impersonation failed: stay read-only */ }

        return new ClientIdentity(pid, image, user, access);
    }

    private static bool IsAdministrator(WindowsIdentity id)
    {
        if (id.IsSystem) return true;
        if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;

        // A non-elevated admin has a filtered ("limited") token whose Administrators SID is deny-only,
        // so IsInRole returns false. TokenElevationTypeLimited means the user *is* an administrator.
        return GetElevationType(id.AccessToken) == TokenElevationType.Limited;
    }

    private static TokenElevationType GetElevationType(SafeAccessTokenHandle token)
    {
        if (!Native.GetTokenInformation(token, Native.TokenInformationClass.TokenElevationType,
                out var type, sizeof(int), out _))
        {
            return TokenElevationType.Default;
        }
        return (TokenElevationType)type;
    }

    private static string? TryGetImagePath(int pid)
    {
        using var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)pid);
        if (process.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    private enum TokenElevationType
    {
        Default = 1,
        Full = 2,
        Limited = 3,
    }

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;

        public enum TokenInformationClass
        {
            TokenElevationType = 18,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformationClass cls,
            out int information, int length, out int returnLength);
    }
}
