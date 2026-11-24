using System.Text.Json;
using Leo.Core;

namespace Leo.App.Services;

/// <summary>Per-user UI preferences: %LocalAppData%\UNTERSTELLEN\LEO\ui.json (LEO.md §8).</summary>
public sealed class UiSettings
{
    private static readonly string FilePath = Path.Combine(LeoPaths.UserData, "ui.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool ShowNotifications { get; set; } = true;

    /// <summary>
    /// The user's "Start LEO at login" choice; null = never chosen, which means on (LEO.md §6.11): the app then
    /// registers itself on first launch, like other tray apps.
    /// </summary>
    public bool? StartAtLogin { get; set; }

    /// <summary>"Later" on an update toast: that version isn't toasted again on that day (M8).</summary>
    public string? PostponedUpdateVersion { get; set; }
    public DateOnly? PostponedUpdateOn { get; set; }

    /// <summary>The update result (LEO.md §24.4) this user was last told about, so it's toasted once.</summary>
    public DateTimeOffset? LastUpdateResultShown { get; set; }

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // corrupt or unreadable: defaults
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(LeoPaths.UserData);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            UiLog.Write("Saving UI settings failed", ex);
        }
    }
}
