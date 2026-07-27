using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Checks for the per-level Cfg growers.
///
/// The bug these exist for: a game grown past seven levels booted, played its
/// title video, then died with two errors naming neither levels nor a file —
///
///     hscore.singe:217: bad argument #1 to 'find' (string expected, got nil)
///     main.singe:4537: attempt to index global 'sprite' (a nil value)
///
/// because readScore() reads exactly finalstage rows off the end of the file and
/// the bundled sample carries seven. readSave() has the identical unchecked loop
/// over the save slots, which ship with twenty-five.
///
/// <see cref="ParsesScores"/> and <see cref="ParsesSlots"/> replay the two
/// readers' own line sequences, so a grown file is checked the way Lua will
/// actually consume it rather than by counting lines.
/// </summary>
public static class LevelCfgFilesTest
{
    public static void Run(Action<string, bool> Check)
    {
        ScoreChecks(Check);
        SlotChecks(Check);
        BundledChecks(Check);
        FolderChecks(Check);
    }

    private static void ScoreChecks(Action<string, bool> Check)
    {
        // ---- The failing case, exactly as it happened ----
        string seven = ScoreSample(7);
        Check("cfg: shipped score sample is 7 rows", LevelCfgFiles.CountScoreRows(seven) == 7);
        Check("cfg: 7 score rows cannot satisfy 9 levels", !ParsesScores(seven, 9));

        string nine = LevelCfgFiles.RepairScores(seven, 9);
        Check("cfg: scores grown to 9 rows", LevelCfgFiles.CountScoreRows(nine) == 9);
        Check("cfg: grown score file parses under 9 levels", ParsesScores(nine, 9));

        // ---- The author's own scores are what must survive ----
        string played = seven.Replace("EGG,20000", "EGG,90000").Replace("ZAK,19000", "ZAK,87000");
        string playedFixed = LevelCfgFiles.RepairScores(played, 9);
        Check("cfg: existing high scores survive",
              playedFixed.Contains("EGG,90000") && playedFixed.Contains("ZAK,87000"));
        Check("cfg: header untouched above the score block",
              playedFixed.StartsWith(string.Join("\n", Split(played).Take(43))));
        Check("cfg: original score rows keep their records", playedFixed.Contains("MAX,80!E"));
        Check("cfg: added score rows are blank records",
              Split(playedFixed).Skip(43 + 7).Where(r => r.Length > 0)
                  .All(r => r == LevelCfgFiles.BlankScoreRow));

        // ---- Grow only ----
        // Nothing reads past finalstage and writeScore rewrites the file at
        // finalstage rows on the next save, so surplus rows are inert. Deleting
        // them would bin the records of a level the author might restore.
        Check("cfg: surplus score rows are kept", LevelCfgFiles.RepairScores(nine, 5) == nine);
        Check("cfg: a surplus score file still parses", ParsesScores(nine, 5));

        // ---- No-ops ----
        Check("cfg: exact score count is left alone", LevelCfgFiles.RepairScores(nine, 9) == nine);
        Check("cfg: zero levels leaves scores alone", LevelCfgFiles.RepairScores(seven, 0) == seven);
        Check("cfg: negative levels leaves scores alone", LevelCfgFiles.RepairScores(seven, -1) == seven);
        Check("cfg: score growth is idempotent",
              LevelCfgFiles.RepairScores(LevelCfgFiles.RepairScores(seven, 9), 9) == nine);
        Check("cfg: a file with no score block is left alone",
              LevelCfgFiles.RepairScores("nothing here\nat all\n", 9) == "nothing here\nat all\n");
        Check("cfg: empty text is left alone", LevelCfgFiles.RepairScores("", 9) == "");

        // ---- Real-world shapes ----
        // The file on disk had no trailing newline: writeScore ends every row with
        // one, but a hand-edited or truncated file need not.
        string noTrailer = seven.TrimEnd('\n');
        Check("cfg: missing trailing newline still counts 7",
              LevelCfgFiles.CountScoreRows(noTrailer) == 7);
        Check("cfg: missing trailing newline grows to 9",
              ParsesScores(LevelCfgFiles.RepairScores(noTrailer, 9), 9));
        Check("cfg: trailing blanks do not hide the score block",
              LevelCfgFiles.CountScoreRows(seven + "\n\n\n") == 7);

        // A CR would ride along inside the difficulty letter — readScore takes it
        // as everything after '!' — so "E\r" would never equal the "E" compared
        // against at hscore.singe:1413.
        string cleaned = LevelCfgFiles.RepairScores(seven.Replace("\n", "\r\n"), 9);
        Check("cfg: CRLF is normalised away", !cleaned.Contains('\r'));
        Check("cfg: CRLF score file still parses after growth", ParsesScores(cleaned, 9));
        Check("cfg: a correctly-sized CRLF file is still cleaned",
              !LevelCfgFiles.RepairScores(seven.Replace("\n", "\r\n"), 7).Contains('\r'));

        // ---- Row recognition ----
        Check("cfg: a score row needs both separators", LevelCfgFiles.IsScoreRow("EGG,80!E"));
        Check("cfg: a plain score line is not a record row", !LevelCfgFiles.IsScoreRow("EGG,20000"));
        Check("cfg: a timer line is not a record row", !LevelCfgFiles.IsScoreRow("t1 = 0"));
        Check("cfg: a dip switch line is not a record row", !LevelCfgFiles.IsScoreRow("dip_Lang = 0"));
    }

    private static void SlotChecks(Action<string, bool> Check)
    {
        string stock = SlotSample(25);
        Check("cfg: shipped save slot is 25 rows", LevelCfgFiles.CountSlotRows(stock) == 25);
        Check("cfg: 25 slot rows cannot satisfy 30 levels", !ParsesSlots(stock, 30));
        Check("cfg: 25 slot rows are fine at 9 levels", ParsesSlots(stock, 9));

        string thirty = LevelCfgFiles.RepairSlots(stock, 30);
        Check("cfg: slots grown to 30 rows", LevelCfgFiles.CountSlotRows(thirty) == 30);
        Check("cfg: grown slot file parses under 30 levels", ParsesSlots(thirty, 30));

        // LvlOrder is a permutation of 1..finalstage; the rows already there are a
        // permutation of 1..count, so continuing the count upward keeps it one.
        List<string> added = Split(thirty).Skip(1 + 25).Where(r => r.Length > 0).ToList();
        Check("cfg: added slot rows continue the level numbering",
              added.Count == 5 && added[0] == "26AfalseBfalseC0D" && added[4] == "30AfalseBfalseC0D");
        Check("cfg: added slot rows are unstarted and unbeaten",
              added.All(r => r.Contains("AfalseBfalse") && r.EndsWith("C0D")));

        // Save progress must survive untouched.
        string progressed = stock
            .Replace("3AfalseBfalseC0D", "3AtrueBtrueC2D")
            .Replace("4AfalseBfalseC0D", "4AtrueBfalseC7D");
        string progressedFixed = LevelCfgFiles.RepairSlots(progressed, 30);
        Check("cfg: beaten levels survive the growth", progressedFixed.Contains("3AtrueBtrueC2D"));
        Check("cfg: death counts survive the growth", progressedFixed.Contains("4AtrueBfalseC7D"));
        Check("cfg: the slot header survives the growth",
              progressedFixed.StartsWith("1,1!1?5;0:0A0B0C0D0E0F\n"));

        Check("cfg: surplus slot rows are kept", LevelCfgFiles.RepairSlots(thirty, 10) == thirty);
        Check("cfg: slot growth is idempotent",
              LevelCfgFiles.RepairSlots(LevelCfgFiles.RepairSlots(stock, 30), 30) == thirty);

        // ---- Row recognition ----
        // The slot header opens "1,1!1?5;0:0A0B…" — it has an 'A' in it and must
        // never be taken for a level row, or the block would swallow the header.
        Check("cfg: a slot row matches the level pattern", LevelCfgFiles.IsSlotRow("26AfalseBfalseC0D"));
        Check("cfg: a beaten slot row matches too", LevelCfgFiles.IsSlotRow("3AtrueBtrueC2D"));
        Check("cfg: the slot header is not a level row",
              !LevelCfgFiles.IsSlotRow("1,1!1?5;0:0A0B0C0D0E0F"));
        Check("cfg: a record row is not a slot row", !LevelCfgFiles.IsSlotRow("EGG,80!E"));
        Check("cfg: a slot file is not read as a score file",
              LevelCfgFiles.CountScoreRows(stock) == 0);
        Check("cfg: a score file is not read as a slot file",
              LevelCfgFiles.CountSlotRows(ScoreSample(7)) == 0);
    }

    /// <summary>The files the app actually ships — if a sample changes shape, fail here, not in a game.</summary>
    private static void BundledChecks(Action<string, bool> Check)
    {
        string[] roots =
        [
            @"C:\Eggmansworld\EggmansLaserForge\assets\DefaultGameFiles\Cfg",
            Path.Combine(AppContext.BaseDirectory, "DefaultGameFiles", "Cfg"),
        ];
        if (roots.FirstOrDefault(Directory.Exists) is not { } cfg)
        {
            Console.WriteLine("  cfg bundled samples: SKIPPED (DefaultGameFiles not found)");
            return;
        }

        string hscore = File.ReadAllText(Path.Combine(cfg, "hscore.cfg"));
        Check("cfg: bundled hscore.cfg is recognised", LevelCfgFiles.CountScoreRows(hscore) > 0);
        Check("cfg: bundled hscore.cfg parses at its own count",
              ParsesScores(hscore, LevelCfgFiles.CountScoreRows(hscore)));
        Check("cfg: bundled hscore.cfg grows to 36 levels",
              ParsesScores(LevelCfgFiles.RepairScores(hscore, 36), 36));

        string slot = File.ReadAllText(Path.Combine(cfg, "s1.cfg"));
        Check("cfg: bundled s1.cfg is recognised as a save slot", LevelCfgFiles.CountSlotRows(slot) > 0);
        Check("cfg: bundled s1.cfg parses at its own count",
              ParsesSlots(slot, LevelCfgFiles.CountSlotRows(slot)));
        Check("cfg: bundled s1.cfg grows to 36 levels",
              ParsesSlots(LevelCfgFiles.RepairSlots(slot, 36), 36));

        // game.cfg is plain "key = value" and must never be classified as either.
        string game = File.ReadAllText(Path.Combine(cfg, "game.cfg"));
        Check("cfg: bundled game.cfg is neither kind",
              LevelCfgFiles.CountScoreRows(game) == 0 && LevelCfgFiles.CountSlotRows(game) == 0);
    }

    /// <summary>The on-disk half, against a throwaway game folder shaped like the real one that failed.</summary>
    private static void FolderChecks(Action<string, bool> Check)
    {
        string game = Path.Combine(Path.GetTempPath(), "ldp-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(game);
            Check("cfg folder: a folder with no Cfg is a no-op",
                  LevelCfgFiles.RepairFolder(game, 9) == null);

            string cfg = Path.Combine(game, "Cfg");
            Directory.CreateDirectory(cfg);
            string hscore = Path.Combine(cfg, "hscore.cfg");
            string slot = Path.Combine(cfg, "s1.cfg");
            string dips = Path.Combine(cfg, "game.cfg");

            // The real folder's shape: seven record rows, no trailing newline.
            File.WriteAllText(hscore, ScoreSample(7).TrimEnd('\n'));
            File.WriteAllText(slot, SlotSample(25));
            File.WriteAllText(dips, "dip_Lang = 0\ndip_Res = 0\n");
            string dipsBefore = File.ReadAllText(dips);

            string? note = LevelCfgFiles.RepairFolder(game, 30);
            Check("cfg folder: reports both files grown",
                  note != null && note.Contains("hscore.cfg") && note.Contains("s1.cfg"));
            Check("cfg folder: does not touch game.cfg",
                  note != null && !note.Contains("game.cfg") && File.ReadAllText(dips) == dipsBefore);

            string hscoreAfter = File.ReadAllText(hscore);
            string slotAfter = File.ReadAllText(slot);
            Check("cfg folder: hscore.cfg parses under 30 levels", ParsesScores(hscoreAfter, 30));
            Check("cfg folder: s1.cfg parses under 30 levels", ParsesSlots(slotAfter, 30));
            Check("cfg folder: written with Unix line endings",
                  !hscoreAfter.Contains('\r') && !slotAfter.Contains('\r'));

            Check("cfg folder: a second run changes nothing",
                  LevelCfgFiles.RepairFolder(game, 30) == null);
            Check("cfg folder: second run left the files byte-identical",
                  File.ReadAllText(hscore) == hscoreAfter && File.ReadAllText(slot) == slotAfter);
            Check("cfg folder: dropping the level count changes nothing",
                  LevelCfgFiles.RepairFolder(game, 4) == null);
            Check("cfg folder: zero levels changes nothing",
                  LevelCfgFiles.RepairFolder(game, 0) == null);
        }
        finally
        {
            try { Directory.Delete(game, recursive: true); } catch { /* temp folder */ }
        }
    }

    /// <summary>
    /// Replays readScore()'s reads: ten scores, a blank, ten more, a blank, ten
    /// percentages, a blank, t1..t4, a blank, t1l..t4l, a blank, then one record
    /// per level. True when every read returns a line carrying the separators its
    /// string.find calls look for.
    /// </summary>
    private static bool ParsesScores(string text, int finalStage)
    {
        List<string> lines = Split(text);
        int at = 0;
        string? Read() => at < lines.Count ? lines[at++] : null;

        for (int block = 0; block < 3; block++)
        {
            for (int k = 0; k < 10; k++)
                if (Read() is not { } row || !row.Contains(',')) return false;
            if (Read() == null) return false; // separator
        }
        for (int block = 0; block < 2; block++)
        {
            for (int k = 0; k < 4; k++)
                if (Read() is not { } t || !t.Contains('=')) return false;
            if (Read() == null) return false; // separator
        }
        for (int k = 0; k < finalStage; k++)
            if (Read() is not { } stage || !stage.Contains(',') || !stage.Contains('!')) return false;

        return true;
    }

    /// <summary>
    /// Replays readSave()'s reads: one header line, then one row per level, each
    /// of which service.singe:4622 splits on 'A', 'B', 'C' and 'D'.
    /// </summary>
    private static bool ParsesSlots(string text, int finalStage)
    {
        List<string> lines = Split(text);
        int at = 0;
        string? Read() => at < lines.Count ? lines[at++] : null;

        if (Read() is not { } header || !header.Contains('F')) return false;
        for (int k = 0; k < finalStage; k++)
            if (Read() is not { } row || !LevelCfgFiles.IsSlotRow(row)) return false;

        return true;
    }

    /// <summary>A well-formed hscore.cfg carrying <paramref name="rows"/> record rows.</summary>
    private static string ScoreSample(int rows)
    {
        string[] names = ["EGG", "ZAK", "DBX", "PAC", "WID", "RHI", "MAX", "GOJ", "LUG", "MAG"];
        var sb = new System.Text.StringBuilder();

        for (int k = 0; k < 10; k++) sb.Append(names[k]).Append(',').Append(20000 - k * 1000).Append('\n');
        sb.Append('\n');
        for (int k = 0; k < 10; k++) sb.Append(names[k]).Append(',').Append(20000 - k * 1000).Append('\n');
        sb.Append('\n');
        for (int k = 0; k < 10; k++) sb.Append(names[k]).Append(',').Append(40 - k * 3).Append('\n');
        sb.Append('\n');
        for (int k = 1; k <= 4; k++) sb.Append('t').Append(k).Append(" = 0\n");
        sb.Append('\n');
        for (int k = 1; k <= 4; k++) sb.Append('t').Append(k).Append("l = 0\n");
        sb.Append('\n');
        for (int k = 0; k < rows; k++) sb.Append(names[k % names.Length]).Append(",80!E\n");

        return sb.ToString();
    }

    /// <summary>A well-formed save slot carrying <paramref name="rows"/> level rows.</summary>
    private static string SlotSample(int rows)
    {
        var sb = new System.Text.StringBuilder("1,1!1?5;0:0A0B0C0D0E0F\n");
        for (int k = 1; k <= rows; k++) sb.Append(LevelCfgFiles.BlankSlotRow(k)).Append('\n');
        return sb.ToString();
    }

    private static List<string> Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();
}
