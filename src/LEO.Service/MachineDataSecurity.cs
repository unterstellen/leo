using System.Security.AccessControl;
using System.Security.Principal;
using Leo.Core;

namespace Leo.Service;

/// <summary>
/// Locks %ProgramData%\UNTERSTELLEN\LEO down to SYSTEM + Administrators (write) and Users (read) — LEO.md §8.
/// Only applied when running as SYSTEM, so dev console runs don't fight over the ACL.
/// </summary>
internal static class MachineDataSecurity
{
    public static void Apply()
    {
        try
        {
            using var self = WindowsIdentity.GetCurrent();
            if (!self.IsSystem) return;

            var dir = new DirectoryInfo(LeoPaths.MachineData);
            dir.Create();

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch
        {
            // Never block startup on ACL hardening.
        }
    }
}
