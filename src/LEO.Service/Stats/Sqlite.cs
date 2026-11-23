using System.Runtime.InteropServices;
using System.Text;

namespace Leo.Service.Stats;

/// <summary>
/// Minimal SQLite access through <c>winsqlite3.dll</c>, the SQLite build that ships with Windows 10/11 (System32).
/// LEO bundles no SQLite binary and needs no extra package (LEO.md §19); SQLite itself is public domain, which
/// fits GPL-3.0-or-later. Only what stats.db needs: open, exec, prepared statements with text/int64 values.
/// </summary>
internal sealed class SqliteDb : IDisposable
{
    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_CREATE = 0x4, SQLITE_OPEN_FULLMUTEX = 0x10000;
    private static readonly IntPtr Transient = new(-1);

    private IntPtr _db;

    public SqliteDb(string path)
    {
        var rc = sqlite3_open_v2(Utf8(path), out _db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            var message = _db != IntPtr.Zero ? Error() : $"error {rc}";
            if (_db != IntPtr.Zero) sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
            throw new InvalidOperationException($"Couldn't open {Path.GetFileName(path)}: {message}");
        }
        sqlite3_busy_timeout(_db, 2000);
    }

    public void Exec(string sql)
    {
        var rc = sqlite3_exec(_db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var err);
        if (rc != SQLITE_OK)
        {
            var message = err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) : Error();
            if (err != IntPtr.Zero) sqlite3_free(err);
            throw new InvalidOperationException($"SQLite: {message}");
        }
    }

    /// <summary>Runs a statement for each parameter row (inside the caller's transaction, if any).</summary>
    public void Run(string sql, params object?[] args)
    {
        using var s = Prepare(sql);
        s.Bind(args);
        s.StepDone();
    }

    public Statement Prepare(string sql)
    {
        var bytes = Utf8(sql);
        var rc = sqlite3_prepare_v2(_db, bytes, bytes.Length, out var stmt, IntPtr.Zero);
        if (rc != SQLITE_OK) throw new InvalidOperationException($"SQLite: {Error()}");
        return new Statement(this, stmt);
    }

    public IEnumerable<Statement> Query(string sql, params object?[] args)
    {
        using var s = Prepare(sql);
        s.Bind(args);
        while (s.Step()) yield return s;
    }

    private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "unknown error";

    private static byte[] Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1]; // zero-terminated
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    internal sealed class Statement(SqliteDb db, IntPtr stmt) : IDisposable
    {
        private IntPtr _stmt = stmt;

        public void Bind(object?[] args)
        {
            sqlite3_reset(_stmt);
            for (var i = 0; i < args.Length; i++)
            {
                var rc = args[i] switch
                {
                    null => sqlite3_bind_null(_stmt, i + 1),
                    long l => sqlite3_bind_int64(_stmt, i + 1, l),
                    int n => sqlite3_bind_int64(_stmt, i + 1, n),
                    string s => BindText(i + 1, s),
                    var other => BindText(i + 1, Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                };
                if (rc != SQLITE_OK) throw new InvalidOperationException($"SQLite bind: {db.Error()}");
            }
        }

        private int BindText(int index, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            return sqlite3_bind_text(_stmt, index, b, b.Length, Transient);
        }

        public bool Step()
        {
            var rc = sqlite3_step(_stmt);
            if (rc == SQLITE_ROW) return true;
            if (rc == SQLITE_DONE) return false;
            throw new InvalidOperationException($"SQLite: {db.Error()}");
        }

        public void StepDone()
        {
            while (Step()) { }
        }

        public long Int64(int column) => sqlite3_column_int64(_stmt, column);

        public string Text(int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(_stmt, column)) ?? "";

        public void Dispose()
        {
            if (_stmt == IntPtr.Zero) return;
            sqlite3_finalize(_stmt);
            _stmt = IntPtr.Zero;
        }
    }

    // ── winsqlite3.dll (System32) ────────────────────────────────────────────

    private const string Lib = "winsqlite3.dll";

    [DllImport(Lib)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
    [DllImport(Lib)] private static extern void sqlite3_free(IntPtr p);
    [DllImport(Lib)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int n, IntPtr destructor);
    [DllImport(Lib)] private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib)] private static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] private static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] private static extern long sqlite3_column_int64(IntPtr stmt, int column);
    [DllImport(Lib)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
}
