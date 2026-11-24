using System.Runtime.InteropServices;
using System.Text;

namespace Leo.Service.Updates;

/// <summary>Product identity of an MSI file, read from its Property table (nothing is executed).</summary>
public sealed record MsiIdentity(string? ProductName, string? ProductVersion, string? UpgradeCode, string? Manufacturer);

/// <summary>Reads an MSI's Property table with msi.dll (<c>MsiOpenDatabase</c>, read-only).</summary>
public static class MsiProperties
{
    public static MsiIdentity? Read(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero /* MSIDBOPEN_READONLY */, out var db) != 0) return null;
        try
        {
            return new MsiIdentity(Get(db, "ProductName"), Get(db, "ProductVersion"), Get(db, "UpgradeCode"), Get(db, "Manufacturer"));
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    private static string? Get(IntPtr db, string name)
    {
        if (MsiDatabaseOpenView(db, $"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'", out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0) return null;
            if (MsiViewFetch(view, out var record) != 0) return null;
            try
            {
                var size = 256;
                var sb = new StringBuilder(size);
                var rc = MsiRecordGetString(record, 1, sb, ref size);
                if (rc == 234 /* ERROR_MORE_DATA */)
                {
                    sb = new StringBuilder(++size);
                    rc = MsiRecordGetString(record, 1, sb, ref size);
                }
                return rc == 0 ? sb.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref int size);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);
}
