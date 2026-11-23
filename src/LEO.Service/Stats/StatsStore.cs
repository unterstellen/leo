using Leo.Core.Stats;

namespace Leo.Service.Stats;

/// <summary>Bytes to add to one day's total.</summary>
public sealed record AdapterDelta(string Day, string AdapterId, string NetworkKey, string AdapterName, long Received, long Sent);

public sealed record AppDelta(string Day, string App, string AdapterId, string NetworkKey, long Received, long Sent);

public sealed record AdapterTotal(string AdapterId, string AdapterName, long Received, long Sent);

public sealed record AppTotal(string App, string AdapterId, long Received, long Sent);

/// <summary>
/// stats.db (LEO.md §8, §19): daily byte totals per adapter/network and per app/network. Nothing else: no
/// connections, destinations, domains or timestamps finer than a day are ever written. Thread-safe.
/// </summary>
public sealed class StatsStore : IDisposable
{
    private const int Schema = 1;
    private readonly Lock _gate = new();
    private readonly SqliteDb _db;

    public StatsStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteDb(path);
        _db.Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS adapter_daily(
                day TEXT NOT NULL, adapter_id TEXT NOT NULL, network_key TEXT NOT NULL, adapter_name TEXT NOT NULL,
                rx INTEGER NOT NULL, tx INTEGER NOT NULL, PRIMARY KEY(day, adapter_id, network_key));
            CREATE TABLE IF NOT EXISTS app_daily(
                day TEXT NOT NULL, app TEXT NOT NULL, adapter_id TEXT NOT NULL, network_key TEXT NOT NULL,
                rx INTEGER NOT NULL, tx INTEGER NOT NULL, PRIMARY KEY(day, app, adapter_id, network_key));
            """);
        _db.Run("INSERT OR IGNORE INTO meta(key, value) VALUES('schema', ?)", Schema.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Add(IReadOnlyCollection<AdapterDelta> adapters, IReadOnlyCollection<AppDelta> apps)
    {
        if (adapters.Count == 0 && apps.Count == 0) return;
        lock (_gate)
        {
            _db.Exec("BEGIN");
            try
            {
                using (var s = _db.Prepare("""
                    INSERT INTO adapter_daily(day, adapter_id, network_key, adapter_name, rx, tx) VALUES(?, ?, ?, ?, ?, ?)
                    ON CONFLICT(day, adapter_id, network_key) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx, adapter_name = excluded.adapter_name
                    """))
                {
                    foreach (var d in adapters)
                    {
                        s.Bind([d.Day, d.AdapterId, d.NetworkKey, d.AdapterName, d.Received, d.Sent]);
                        s.StepDone();
                    }
                }
                using (var s = _db.Prepare("""
                    INSERT INTO app_daily(day, app, adapter_id, network_key, rx, tx) VALUES(?, ?, ?, ?, ?, ?)
                    ON CONFLICT(day, app, adapter_id, network_key) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx
                    """))
                {
                    foreach (var d in apps)
                    {
                        s.Bind([d.Day, d.App, d.AdapterId, d.NetworkKey, d.Received, d.Sent]);
                        s.StepDone();
                    }
                }
                _db.Exec("COMMIT");
            }
            catch
            {
                try { _db.Exec("ROLLBACK"); } catch { }
                throw;
            }
        }
    }

    /// <summary>Per-adapter totals for days ≥ <paramref name="fromDay"/> (all networks of the adapter together).</summary>
    public IReadOnlyList<AdapterTotal> AdapterTotals(string fromDay)
    {
        lock (_gate)
        {
            return _db.Query("""
                SELECT adapter_id, MAX(adapter_name), SUM(rx), SUM(tx) FROM adapter_daily WHERE day >= ? GROUP BY adapter_id
                """, fromDay).Select(r => new AdapterTotal(r.Text(0), r.Text(1), r.Int64(2), r.Int64(3))).ToList();
        }
    }

    public IReadOnlyList<AppTotal> AppTotals(string fromDay)
    {
        lock (_gate)
        {
            return _db.Query("""
                SELECT app, adapter_id, SUM(rx), SUM(tx) FROM app_daily WHERE day >= ? GROUP BY app, adapter_id
                """, fromDay).Select(r => new AppTotal(r.Text(0), r.Text(1), r.Int64(2), r.Int64(3))).ToList();
        }
    }

    /// <summary>Deletes rows older than the retention (LEO.md §19). Returns rows removed.</summary>
    public int Prune(DateOnly today)
    {
        lock (_gate)
        {
            var before = Count();
            _db.Run("DELETE FROM adapter_daily WHERE day < ?", StatsRetention.Cutoff(today, StatsRetention.AdapterDays));
            _db.Run("DELETE FROM app_daily WHERE day < ?", StatsRetention.Cutoff(today, StatsRetention.AppDays));
            return before - Count();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _db.Exec("DELETE FROM adapter_daily; DELETE FROM app_daily;");
            try { _db.Exec("VACUUM"); } catch (InvalidOperationException) { }
        }
    }

    private int Count() => (int)(_db.Query("SELECT (SELECT COUNT(*) FROM adapter_daily) + (SELECT COUNT(*) FROM app_daily)").Select(r => r.Int64(0)).First());

    /// <summary>Distinct days stored (tests).</summary>
    internal IReadOnlyList<string> Days(string table)
    {
        lock (_gate) return _db.Query($"SELECT DISTINCT day FROM {(table == "app" ? "app_daily" : "adapter_daily")} ORDER BY day").Select(r => r.Text(0)).ToList();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
