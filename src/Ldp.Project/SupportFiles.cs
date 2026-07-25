namespace Ldp.Project;

/// <summary>
/// Copies the support files a Singe game needs — Cfg, Fonts, Overlay, Script,
/// Sounds — into a game folder.
///
/// They are not optional. The framework calls readConfig() at boot and reads
/// Cfg/game.cfg with a bare io.input and no existence check, so a game folder
/// without one quits Hypseus before it shows a frame. Authors normally gather
/// these by copying another game's folder; the app ships a generic set instead.
///
/// Nothing is ever overwritten. The picks are deliberately swappable, so an
/// author who has replaced an overlay, changed the font, or retuned hscore.cfg
/// against a modified scoring system keeps their work — running this again
/// fills in only what is genuinely absent.
/// </summary>
public static class SupportFiles
{
    /// <summary>
    /// Copies every file under <paramref name="sourceRoot"/> that the game
    /// folder doesn't already have, preserving the folder layout. Returns the
    /// relative paths added, so a caller can report what it put there instead
    /// of writing into the author's game silently. A missing source root is a
    /// no-op (a dev build may not have the files bundled).
    /// </summary>
    public static List<string> InstallFrom(string sourceRoot, string gameFolder)
    {
        List<string> added = [];
        if (!Directory.Exists(sourceRoot)) return added;
        Directory.CreateDirectory(gameFolder);

        foreach (string source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            string target = Path.Combine(gameFolder, relative);
            if (File.Exists(target)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            added.Add(relative);
        }
        return added;
    }

    /// <summary>Short summary naming the areas touched, e.g. "12 support file(s) added (Cfg, Sounds)".</summary>
    public static string Describe(IReadOnlyList<string> added)
    {
        if (added.Count == 0) return "";
        IEnumerable<string> areas = added
            .Select(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0])
            .Distinct()
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase);
        return $"{added.Count} support file(s) added ({string.Join(", ", areas)})";
    }
}
