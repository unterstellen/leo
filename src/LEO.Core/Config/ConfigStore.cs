using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leo.Core.Config;

/// <summary>Thread-safe load/mutate/save of <see cref="LeoConfig"/> with atomic file replacement.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private LeoConfig _config;

    public ConfigStore(string path)
    {
        _path = path;
        _config = Load(path);
        var before = _config.SchemaVersion;
        if (ConfigMigration.Upgrade(_config))
        {
            // Keep the file as it was before an upgrade (once per schema), then save the upgraded one.
            try
            {
                var backup = $"{path}.v{before}.bak";
                if (File.Exists(path) && before < ConfigMigration.CurrentSchema && !File.Exists(backup)) File.Copy(path, backup);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Save(path, _config); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { } // read-only callers (tests, tools) keep the upgraded copy in memory
        }
    }

    public string Path => _path;

    public event EventHandler? Changed;

    /// <summary>Read under the lock. Don't hold on to mutable parts of the returned object.</summary>
    public T Read<T>(Func<LeoConfig, T> read)
    {
        lock (_gate) return read(_config);
    }

    /// <summary>Apply a change, persist it, and notify. Returns false if <paramref name="mutate"/> reports no change.</summary>
    public bool Update(Func<LeoConfig, bool> mutate)
    {
        lock (_gate)
        {
            if (!mutate(_config)) return false;
            Save(_path, _config);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static LeoConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LeoConfig>(File.ReadAllText(path), Json) ?? new LeoConfig();
            }
        }
        catch (Exception)
        {
            // Corrupt config: keep a copy for diagnosis and start fresh rather than failing to boot.
            try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
        }
        return new LeoConfig();
    }

    private static void Save(string path, LeoConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        File.Move(temp, path, overwrite: true);
    }
}
