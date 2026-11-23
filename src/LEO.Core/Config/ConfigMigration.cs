using Leo.Core.Profiles;

namespace Leo.Core.Config;

/// <summary>
/// Upgrades older config.json files in place (LEO.md §19). Never drops user data: v1 → v2 only adds the
/// "Default" profile around the existing settings, which stay where they are (they are the active profile's).
/// </summary>
public static class ConfigMigration
{
    public const int CurrentSchema = 2;

    /// <summary>Upgrades and repairs <paramref name="c"/>; returns true when it changed (the caller saves it).</summary>
    public static bool Upgrade(LeoConfig c)
    {
        var changed = false;
        if (c.SchemaVersion < 2)
        {
            // v1 had no profiles: everything the user set up becomes the active "Default" profile.
            c.Profiles ??= [];
            c.ProfileState = new ProfileState { ActiveProfileId = Profile.DefaultId };
            changed = true;
        }
        changed |= Repair(c);
        if (c.SchemaVersion != CurrentSchema && c.SchemaVersion < CurrentSchema)
        {
            c.SchemaVersion = CurrentSchema;
            changed = true;
        }
        return changed;
    }

    /// <summary>Invariants: Default exists (listed last), ids are unique, the active profile exists.</summary>
    private static bool Repair(LeoConfig c)
    {
        var changed = false;
        c.Profiles ??= [];
        c.ProfileState ??= new ProfileState();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < c.Profiles.Count; i++)
        {
            var p = c.Profiles[i];
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || !seen.Add(p.Id))
            {
                c.Profiles.RemoveAt(i--);
                changed = true;
                continue;
            }
            p.Settings ??= new ProfileSettings();
            p.Triggers ??= [];
            if (string.IsNullOrWhiteSpace(p.Name)) { p.Name = p.IsDefault ? "Default" : "Profile"; changed = true; }
        }

        var def = c.Profiles.FirstOrDefault(p => p.IsDefault);
        if (def is null)
        {
            c.Profiles.Add(new Profile { Id = Profile.DefaultId, Name = "Default" });
            changed = true;
        }
        else if (c.Profiles[^1] != def)
        {
            c.Profiles.Remove(def);
            c.Profiles.Add(def);
            changed = true;
        }
        if (c.Profiles[^1].Triggers.Count > 0) { c.Profiles[^1].Triggers.Clear(); changed = true; }

        if (c.Profiles.All(p => p.Id != c.ProfileState.ActiveProfileId))
        {
            // The live settings stay as they are; they now count as Default's.
            c.ProfileState.ActiveProfileId = Profile.DefaultId;
            c.ProfileState.Manual = false;
            changed = true;
        }
        return changed;
    }
}
