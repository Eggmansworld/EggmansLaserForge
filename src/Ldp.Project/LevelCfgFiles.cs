using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>
/// Keeps the Cfg files that carry one row per level in step with the level count.
///
/// Two of the framework's readers walk exactly finalstage rows and never check
/// for end of file:
///
///     hscore.singe:214   readScore()  -- Cfg/hscore.cfg, the per-level records
///     service.singe:4618 readSave()   -- Cfg/s1..s6.cfg, the save slots
///
/// Both then hand the line straight to string.find, so a file holding fewer rows
/// than finalstage dies during boot with an argument error naming neither levels
/// nor the file:
///
///     hscore.singe:217: bad argument #1 to 'find' (string expected, got nil)
///
/// The throw lands inside onOverlayUpdate, which aborts before the sprite table
/// is built, so the next overlay tick raises a second, unrelated-looking error
/// (main.singe:4537, indexing a nil 'sprite') and Singe quits with "Multiple
/// errors, cannot continue". The title video plays first, which makes the whole
/// thing read like a video problem.
///
/// The bundled files are samples — seven rows in hscore.cfg, twenty-five in each
/// save slot — and <see cref="SupportFiles"/> never overwrites a file already in
/// the game folder, correctly, since these hold the author's scores and saves.
/// So a game simply stops booting on the day it grows past those counts.
///
/// Rows are only ever ADDED. Nothing in the framework reads past finalstage, and
/// both writers rewrite their whole file at finalstage rows on the next save, so
/// surplus rows are inert and self-clearing. Deleting them here would throw away
/// the records and progress of levels an author may well put back.
/// </summary>
public static class LevelCfgFiles
{
    /// <summary>
    /// The hscore.cfg row for a level with no record yet: name, percent to beat,
    /// difficulty letter. Matches the bundled sample so a grown file stays
    /// homogeneous. 80 is the framework author's chosen target — a player has to
    /// clear more than that on the level to register.
    /// </summary>
    public const string BlankScoreRow = "EGG,80!E";

    /// <summary>
    /// The save-slot row for an untouched level: play-order index, started flag,
    /// beaten flag, death count. The leading number is LvlOrder[k], which the
    /// framework treats as a permutation of 1..finalstage — appending the next
    /// index up keeps it one, because the rows already present are a permutation
    /// of 1..count.
    /// </summary>
    public static string BlankSlotRow(int level) => $"{level}AfalseBfalseC0D";

    /// <summary>A per-level record row. The other lines in hscore.cfg carry at most one separator.</summary>
    public static bool IsScoreRow(string line) => line.Contains(',') && line.Contains('!');

    /// <summary>
    /// A save-slot level row. Anchored, so the slot's header line — which opens
    /// "1,1!1?5;0:0A0B…" and does contain an 'A' — cannot be mistaken for one.
    /// </summary>
    public static bool IsSlotRow(string line) => SlotRow.IsMatch(line);

    private static readonly Regex SlotRow =
        new(@"^\d+A(true|false)B(true|false)C\d+D$", RegexOptions.Compiled);

    public static int CountScoreRows(string text) => Block(Split(text), IsScoreRow).Count;

    public static int CountSlotRows(string text) => Block(Split(text), IsSlotRow).Count;

    /// <summary>Grows hscore.cfg text to at least <paramref name="finalStage"/> record rows.</summary>
    public static string RepairScores(string text, int finalStage) =>
        Grow(text, finalStage, IsScoreRow, _ => BlankScoreRow);

    /// <summary>Grows save-slot text to at least <paramref name="finalStage"/> level rows.</summary>
    public static string RepairSlots(string text, int finalStage) =>
        Grow(text, finalStage, IsSlotRow, BlankSlotRow);

    /// <summary>
    /// Grows every per-level Cfg file in <paramref name="gameFolder"/> that is
    /// short of <paramref name="finalStage"/> rows. Returns a note naming what
    /// changed, or null when there was nothing to do.
    ///
    /// Files are classified by what they end with rather than by name, so the
    /// altCfg variants (hscore2.cfg, s10.cfg…) are covered without enumerating
    /// them, and game.cfg — plain "key = value" lines — is never a candidate.
    /// </summary>
    public static string? RepairFolder(string gameFolder, int finalStage)
    {
        string cfg = Path.Combine(gameFolder, "Cfg");
        if (finalStage <= 0 || !Directory.Exists(cfg)) return null;

        List<string> grown = [];
        foreach (string path in Directory.GetFiles(cfg, "*.cfg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text = File.ReadAllText(path);
            string repaired =
                CountScoreRows(text) > 0 ? RepairScores(text, finalStage) :
                CountSlotRows(text) > 0 ? RepairSlots(text, finalStage) :
                text;
            if (repaired == text) continue;

            File.WriteAllText(path, repaired);
            grown.Add(Path.GetFileName(path));
        }

        if (grown.Count == 0) return null;
        return $"Grew Cfg/{string.Join(", Cfg/", grown)} to {finalStage} level rows — " +
               "the framework reads one line per level at boot and quits if any are missing.";
    }

    /// <summary>
    /// Appends rows until the trailing block reaches <paramref name="finalStage"/>.
    /// Unchanged when it already does, when there is nothing to size against, or
    /// when the text has no such block — an unrecognised file is the author's.
    ///
    /// A file that is rewritten comes back with LF endings, which is what both
    /// framework writers produce. A stray CR would otherwise ride along inside
    /// the last field on its line: readScore takes the difficulty letter as
    /// everything after '!', so "E\r" would never equal the "E" it is compared
    /// against at hscore.singe:1413.
    /// </summary>
    private static string Grow(string text, int finalStage, Func<string, bool> isRow, Func<int, string> blankRow)
    {
        if (finalStage <= 0) return text;

        List<string> lines = Split(text);
        (int start, int count) = Block(lines, isRow);
        if (count == 0) return text;
        if (count >= finalStage && !text.Contains('\r')) return text;

        // Sliced by where the block starts, not by how far it sits from the end:
        // text ending in a newline splits to a trailing empty entry, and counting
        // back from there would strand the first row up in the header.
        List<string> rebuilt = lines.Take(start + count).ToList();
        for (int level = count + 1; level <= finalStage; level++) rebuilt.Add(blankRow(level));

        return string.Join("\n", rebuilt) + "\n";
    }

    /// <summary>
    /// Where the trailing run of level rows begins and how long it is. Blank
    /// lines at the very end are stepped over rather than counted.
    /// </summary>
    private static (int Start, int Count) Block(List<string> lines, Func<string, bool> isRow)
    {
        int end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0) end--;

        int start = end;
        while (start > 0 && isRow(lines[start - 1])) start--;
        return (start, end - start);
    }

    private static List<string> Split(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
}
