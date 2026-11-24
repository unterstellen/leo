using System.Security.AccessControl;
using System.Security.Principal;
using Leo.Core;

namespace Leo.Service;

/// <summary>
/// Locks LEO's machine data down (LEO.md §8, §24.6):
/// <list type="bullet">
/// <item><c>%ProgramData%\UNTERSTELLEN</c> and <c>…\LEO</c>: owned by SYSTEM or Administrators, never a junction or
/// symlink; SYSTEM + Administrators write, Users read (config, stats and logs stay readable on purpose).</item>
/// <item><c>…\LEO\engine</c> and <c>…\LEO\updates</c>: SYSTEM + Administrators only (the engine config, its cache
/// and rule lists; downloaded MSIs).</item>
/// </list>
/// A standard user can create folders in C:\ProgramData, so one could pre-create <c>UNTERSTELLEN</c> before LEO is
/// installed and redirect SYSTEM's writes. Such a folder gets its owner and ACL reset (SYSTEM may); if that fails,
/// or it's a reparse point that can't be removed, the service refuses to use it.
/// Only applied when running as SYSTEM, so dev console runs don't fight over the ACL.
/// </summary>
internal static class MachineDataSecurity
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Result of <see cref="Apply"/>: false = the data folders couldn't be made safe; don't write there.</summary>
    public static bool Apply()
    {
        try
        {
            using var self = WindowsIdentity.GetCurrent();
            if (!self.IsSystem) return true;

            return Secure(LeoPaths.MachinePublisherDir, MachineFolderSecurity())
                   && Secure(LeoPaths.MachineData, MachineFolderSecurity())
                   && Secure(LeoPaths.EngineDir, PrivateFolderSecurity())
                   && Secure(LeoPaths.UpdatesDir, PrivateFolderSecurity());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LEO: securing {LeoPaths.MachineData} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Creates or repairs one folder: no reparse point, trusted owner, the given protected ACL.</summary>
    private static bool Secure(string path, DirectorySecurity wanted)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists && IsReparsePoint(dir))
        {
            // A junction/symlink planted in place of LEO's folder: remove the link itself (never its target).
            dir.Delete(recursive: false);
            dir.Refresh();
        }
        dir.Create();
        dir.Refresh();
        if (IsReparsePoint(dir)) return false;

        var current = dir.GetAccessControl(AccessControlSections.Owner);
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrustedOwner(owner))
        {
            wanted.SetOwner(AdminsSid);
        }
        dir.SetAccessControl(wanted);

        dir.Refresh();
        var after = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return after is not null && IsTrustedOwner(after) && !IsReparsePoint(dir);
    }

    /// <summary>Only SYSTEM or the Administrators group may own LEO's data folders.</summary>
    internal static bool IsTrustedOwner(SecurityIdentifier owner) => owner == SystemSid || owner == AdminsSid;

    internal static bool IsReparsePoint(FileSystemInfo info) => info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>SYSTEM + Administrators full control, Users read and execute, protected from inheritance (§8).</summary>
    internal static DirectorySecurity MachineFolderSecurity()
    {
        var security = PrivateFolderSecurity();
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>SYSTEM + Administrators only, protected from inheritance (engine folder, updates folder, §24.6).</summary>
    internal static DirectorySecurity PrivateFolderSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
}
