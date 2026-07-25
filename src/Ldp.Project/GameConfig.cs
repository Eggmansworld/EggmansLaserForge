namespace Ldp.Project;

/// <summary>
/// The game's persisted service-menu settings, which Singe keeps in
/// <c>Cfg/game.cfg</c> inside the game folder and rewrites itself.
///
/// They outlive any change made here, and that is the trap: a start level the
/// project no longer has makes the framework read <c>Level[n]</c> as nil and
/// die with "attempt to index field '?' (a nil value)" at main.singe:6600 —
/// with nothing to connect the crash back to a dip switch set days earlier.
///
/// The file is NOT optional and is NOT regenerated. <c>main.singe:5272</c>
/// calls <c>readConfig()</c> at boot, which does a bare
/// <c>io.input(MYDIR.."/Cfg/game.cfg")</c> with no existence check (there is no
/// <c>io.open</c> anywhere in the framework); a missing file quits Hypseus with
/// "bad argument #1 to 'input'". Only <c>setDefault()</c> reads default.cfg,
/// and nothing but the service menu ever calls it. So the fix for a bad value
/// is to EDIT game.cfg, never to delete it.
/// </summary>
public static class GameConfig
{
    /// <summary>Where Singe keeps the settings for a game folder.</summary>
    public static string PathFor(string gameFolder) =>
        System.IO.Path.Combine(gameFolder, "Cfg", "game.cfg");

    /// <summary>Reads a <c>name = number</c> setting, or null when it isn't there.</summary>
    public static int? Value(string cfgText, string name)
    {
        foreach (string raw in cfgText.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (!line[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line[(eq + 1)..].Trim(), out int value)) return value;
        }
        return null;
    }

    /// <summary>
    /// Settings the project can no longer satisfy. Empty when the file is
    /// absent or every value is in range — an author who never opened the
    /// service menu is never nagged.
    /// </summary>
    public static List<string> Validate(string cfgText, LdpProject project)
    {
        List<string> warnings = [];
        int levels = project.Levels.Count;

        int startLevel = Value(cfgText, "dip_StartLevel") ?? 1;
        if (startLevel < 1 || startLevel > levels)
        {
            warnings.Add($"Cfg/game.cfg starts the game at level {startLevel}, but the game has " +
                         $"{levels} level(s). The framework will crash on a level that isn't there - " +
                         "edit dip_StartLevel to 1 in Cfg/game.cfg, or reset Start Level in the service menu. " +
                         "Do not delete the file: the framework reads it at boot without checking it exists.");
            return warnings; // the scene check below needs a level that exists
        }

        int startScene = Value(cfgText, "dip_StartScene") ?? 1;
        int scenes = levels > 0 ? project.Levels[startLevel - 1].SceneIds.Count : 0;
        if (startScene < 1 || startScene > scenes)
            warnings.Add($"Cfg/game.cfg starts the game at scene {startScene} of level {startLevel}, " +
                         $"which has {scenes} scene(s) - edit dip_StartScene to 1 in Cfg/game.cfg, " +
                         "or reset Start Scene in the service menu.");
        return warnings;
    }

    /// <summary>
    /// Same check straight off disk, plus the harder failure: no game.cfg at
    /// all. That is not a "not configured yet" state - the framework quits on
    /// it before showing a frame - so it is reported, not skipped.
    /// </summary>
    public static List<string> ValidateFolder(string gameFolder, LdpProject project)
    {
        try
        {
            string path = PathFor(gameFolder);
            if (System.IO.File.Exists(path)) return Validate(System.IO.File.ReadAllText(path), project);

            string remedy = System.IO.File.Exists(System.IO.Path.Combine(gameFolder, "Cfg", "default.cfg"))
                ? "Copy Cfg/default.cfg to Cfg/game.cfg."
                : "The game folder needs a Cfg folder holding default.cfg and game.cfg - copy one from a working game.";
            return [$"Cfg/game.cfg is missing. The framework reads it at boot without checking it exists " +
                    $"(readConfig, service.singe:41), so Hypseus quits with \"bad argument #1 to 'input'\" " +
                    $"before the game starts. {remedy}"];
        }
        catch (System.IO.IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
