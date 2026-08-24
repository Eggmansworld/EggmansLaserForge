using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Checks for the two ways a community script can import wrong without
/// anything looking broken.
///
/// 1. Menu frames written as arithmetic against a landmark — "frameVictory =
///    offsetMenus +3". A digits-only pattern misses the line entirely, and a
///    missed line is indistinguishable from an absent one, so the slot silently
///    imported as unset.
///
/// 2. Level intro clips holding something that is not an intro. The framework
///    reads Level[n] = {title, start, introEnd, ...} and plays start..introEnd
///    before the level; scripts in the wild put the level's end, or its first
///    scene, in that second slot, producing an "intro" that plays minutes of
///    gameplay.
/// </summary>
public static class ImportFidelityTest
{
    public static void Run(Action<string, bool> Check)
    {
        OffsetChecks(Check);
        IntroChecks(Check);
        ImportChecks(Check);
        DeadlineStyleChecks(Check);
        BranchTableChecks(Check);
        ReimportChecks(Check);
        RelativeFrameChecks(Check);
    }

    /// <summary>
    /// RelativeFrames = true. setupFrames adds Level[thisLevel][INTROCLIP] to
    /// sceneStart, sceneEnd and both frames of every move (main.singe:6392), so
    /// the numbers in setupMoves are offsets into the level rather than disc
    /// frames.
    ///
    /// The importer used to read them as disc frames and say so in the log,
    /// which imported Tron_1982 with every scene of Level 1 sitting 9,749
    /// frames early — and since the export writes RelativeFrames = false, the
    /// framework then added nothing back and the game was unplayable.
    ///
    /// Death[], Level[] and the menu slots are absolute under both settings.
    /// setupFrames never touches them, so folding a base into them would break
    /// the scripts that are already right.
    /// </summary>
    private static void RelativeFrameChecks(Action<string, bool> Check)
    {
        // Tron_1982's Level 1, verbatim: base 9749, first three moves relative.
        const string script = """
            RelativeFrames = true

            Death[1] = {76177, 76196}

            Level[1] = {"Arrival", 9749, 9984, 1, 0, 0, -1}
            Level[2] = {"Ball Game", 13020, 15232, 1, 0, 0, -1}
            Level[levelSecret] = {"Tank", 78327, 78447, 1, 0, 0, -1}

            function setupMoves(thisLevel, thisScene)
                if thisLevel == 1 then
                    if thisScene == 1 then
                        sceneStart = 236
                        sceneEnd   = 2047
                        totalMoves = 3
                        move[1] = {271, 286, UP, 1}
                        move[2] = {321, 514, SKIP, 0}
                        move[3] = {709, 724, LEFT, 1}
                    end
                elseif thisLevel == 2 then
                    if thisScene == 1 then
                        sceneStart = 378
                        sceneEnd   = 775
                        totalMoves = 1
                        move[1] = {400, 420, UP, 1}
                    end
                elseif thisLevel == levelSecret then
                    if thisScene == 1 then
                        sceneStart = 10
                        sceneEnd   = 500
                        totalMoves = 1
                        move[1] = {100, 120, UP, 1}
                    end
                end
            end
            """;

        var project = new LdpProject { Name = "Relative" };
        SingeImporter.Result result = SingeImporter.Import(project, script);

        Clip? one = project.Clips.FirstOrDefault(c => c.Name == "L1 S1");
        Check("relative: sceneStart takes the level's start frame",
              one is { StartFrame: 9985 });                       // 236 + 9749
        Check("relative: sceneEnd takes it too",
              one is { EndFrame: 11796 });                        // 2047 + 9749
        Check("relative: move frames take it as well",
              one != null && one.Interactions.Select(m => m.Frame).SequenceEqual([10020, 10070, 10458]));
        Check("relative: an explicit move window keeps its length",
              one != null && one.Interactions[1].EndFrameOverride == 10263);   // 514 + 9749

        // Each level counts from its OWN start, which is the whole point: a
        // single global offset would put every level but the first wrong.
        Clip? two = project.Clips.FirstOrDefault(c => c.Name == "L2 S1");
        Check("relative: the second level counts from its own start",
              two is { StartFrame: 13398, EndFrame: 13795 });      // 378/775 + 13020
        Check("relative: its moves do the same",
              two is { Interactions.Count: 1 } && two.Interactions[0].Frame == 13420);

        Check("relative: the Death table is left absolute",
              project.DeathPool.Count == 1 &&
              project.Clips.Any(c => c.StartFrame == 76177 && c.EndFrame == 76196));
        Check("relative: the Level table is left absolute",
              project.Levels[0].StartFrame == 9749 && project.Levels[0].IntroEndFrame == 9984);
        Check("relative: the conversion is reported",
              result.Warnings.Any(w => w.Contains("RelativeFrames") && w.Contains("converted")));

        // The secret level is a distinct branch, not more scenes for Level 2.
        // Left unmatched, `elseif thisLevel == levelSecret` kept currentLevel on
        // the level above and quietly handed it the bonus level's scenes — with
        // the wrong base under RelativeFrames, 68,578 frames out in this script.
        Check("relative: the secret level does not join the level above",
              project.Levels[1].SceneIds.Count == 1);
        Check("relative: the secret level is reported, not silently dropped",
              result.Warnings.Any(w => w.Contains("levelSecret")));
        Check("relative: only the real levels are imported", result.Levels == 2);

        // The export must say false, or the framework adds each level's start a
        // second time to numbers that already carry it.
        SingeExporter.Result exported = SingeExporter.Export(project);
        Check("relative: the exported script sets RelativeFrames = false",
              exported.Script.Contains("RelativeFrames = false"));
        SingeTemplate.Result filled = SingeTemplate.Apply(project, """
            RelativeFrames = true
            """);
        Check("relative: a template's RelativeFrames = true is overwritten",
              filled.Script.Contains("RelativeFrames = false"));

        // An absolute script must come through untouched.
        var plain = new LdpProject { Name = "Absolute" };
        SingeImporter.Import(plain, script.Replace("RelativeFrames = true", "RelativeFrames = false"));
        Check("relative: an absolute script keeps its frames",
              plain.Clips.Any(c => c is { Name: "L1 S1", StartFrame: 236, EndFrame: 2047 }));
    }

    /// <summary>
    /// Importing REPLACES the game a project describes; it used to merge.
    ///
    /// The scenes were rebuilt from scratch each time but the levels were
    /// appended, so importing the same file twice produced a 26-level game
    /// whose L14..L26 duplicated L1..L13 — and the levels the author was still
    /// editing went on pointing at the OLD scenes. That is what made a fix in
    /// the importer look like it had not worked: the corrected scenes were
    /// there, just not the ones any level used. Deleting the duplicates did not
    /// help, because the next import appended after the highest number ever
    /// used.
    ///
    /// The videos belong to the project, not the script, and must survive.
    /// </summary>
    private static void ReimportChecks(Action<string, bool> Check)
    {
        const string script = """
            Level[1] = {"One", 1000, 1001, 1, 0, 0, 0}
            Level[2] = {"Two", 3000, 3001, 1, 0, 0, 0}
            function setupMoves(thisLevel, thisScene)
            if thisLevel == 1 then
                if thisScene == 1 then
                    sceneStart = 1100
                    sceneEnd   = 1900
                    totalMoves = 1
                    move[1] = {1200, 1220, UP, 0}
                end
            elseif thisLevel == 2 then
                if thisScene == 1 then
                    sceneStart = 3100
                    sceneEnd   = 3900
                    totalMoves = 1
                    move[1] = {3200, 3220, DOWN, 0}
                end
            end
            end
            """;

        var project = new LdpProject { Name = "Reimport" };
        project.Videos.Add(new VideoSource { Path = "Video/main.m2v", PictureCount = 50000, GlobalBase = 0 });

        SingeImporter.Import(project, script);
        int levels = project.Levels.Count, clips = project.Clips.Count;
        Check("reimport: the first import builds the game", levels == 2 && clips == 2);

        SingeImporter.Import(project, script);
        SingeImporter.Import(project, script);
        Check("reimport: importing again does not append levels", project.Levels.Count == levels);
        Check("reimport: importing again does not append scenes", project.Clips.Count == clips);
        Check("reimport: level titles are not duplicated",
              project.Levels.Select(l => l.Title).SequenceEqual(["One", "Two"]));
        Check("reimport: every scene still belongs to a level",
              project.Levels.SelectMany(l => l.SceneIds).Distinct().Count() == project.Clips.Count);
        Check("reimport: moves are not duplicated inside a scene",
              project.Clips.All(c => c.Interactions.Count == 1));

        // The videos are the project's own.
        Check("reimport: the project's videos survive",
              project.Videos is [{ Path: "Video/main.m2v", PictureCount: 50000 }]);

        // Deleting a level must not leave the next import numbering past it.
        project.Levels.RemoveAt(1);
        SingeImporter.Import(project, script);
        Check("reimport: after deleting a level, the next import is still 2 levels",
              project.Levels.Count == 2 &&
              project.Levels.Select(l => l.Title).SequenceEqual(["One", "Two"]));

        // Scenes an author added by hand are part of the game the script owns,
        // so they go too — the confirm in the UI is what makes that a choice.
        project.Clips.Add(new Clip { Name = "hand-made", StartFrame = 9000, EndFrame = 9100 });
        SingeImporter.Import(project, script);
        Check("reimport: replaces rather than accumulating stray scenes",
              project.Clips.Count == clips && project.Clips.All(c => c.Name != "hand-made"));
    }

    /// <summary>
    /// A branch move is TWO lines, and the importer only ever read the first.
    ///
    ///     move[1] = {7356, 7500, PATH, -1}          -- a decision happens here
    ///     path[1] = {BUTTON1,1039,0,0,0,0,0,0,2}    -- and this is the decision
    ///
    /// (That row reads: press Button 1 and you get target 1039; the framework
    /// treats a target over 1000 as a death, so 1039 is Death[39]. Field 9 is
    /// the move to resume at afterwards.)
    ///
    /// Dropping the second line does not export a game missing a branch — it
    /// exports one that CRASHES. main.singe clears the table per scene
    /// (`path = nil; path = {}`), so a PATH move with no row makes
    /// `path[currentMove][1]` index a nil value.
    ///
    /// The framework indexes the row by the MOVE's number, so it has to travel
    /// with the move rather than with the position it happened to hold in the
    /// script it came from.
    /// </summary>
    private static void BranchTableChecks(Action<string, bool> Check)
    {
        const string script = """
            gap = 10
            offsetDeath = 20000
            Death[39] = {offsetDeath+100, offsetDeath+200}
            Level[1] = {"Forest House", 6906, 6907, 1, 0, 0, 0}
            function setupMoves(thisLevel, thisScene)
            if thisLevel == 1 then
                if thisScene == 1 then
                    sceneStart = 7256
                    sceneEnd   = 7821
                    totalMoves = 3
                    move[1] = {7356, 7500, PATH, -1}
                    move[2] = {7502, 7542, BUTTON1, 39}
                    move[3] = {7752-gap, 7752, BUTTON1, 39}
                    path[1] = {BUTTON1,1039,0,0,0,0,0,0,2}
                end
            end
            end
            """;

        var project = new LdpProject { Name = "Branching" };
        SingeImporter.Result result = SingeImporter.Import(project, script);
        Clip scene = project.Clips.First(c => c.Interactions.Count == 3);
        List<InteractionMarker> moves = scene.Interactions.OrderBy(m => m.Frame).ToList();

        Check("branch: the path row is kept", moves[0].BranchRows?["path"] == "BUTTON1,1039,0,0,0,0,0,0,2");
        Check("branch: it lands on the move it belongs to",
              moves[1].BranchRows == null && moves[2].BranchRows == null);
        Check("branch: the import says the table cannot be edited here",
              result.Warnings.Any(w => w.Contains("branch table", StringComparison.OrdinalIgnoreCase)));

        string exported = SingeExporter.Export(project).Script;
        Check("branch: the row is written back verbatim",
              exported.Contains("path[1] = {BUTTON1,1039,0,0,0,0,0,0,2}", StringComparison.Ordinal));
        Check("branch: a complete branch move draws no export warning",
              !SingeExporter.Export(project).Warnings.Any(w => w.Contains("has no path[", StringComparison.Ordinal)));

        // The row follows its move. Moves are numbered in FRAME order, so adding
        // one ahead of the PATH move makes it move[2] — and its row has to become
        // path[2] or it now describes somebody else's move.
        var reordered = project.Clips.First(c => c.Interactions.Count == 3).Duplicate();
        var project2 = new LdpProject { Name = "Reordered" };
        project2.Clips.Add(reordered);
        project2.AddLevel().SceneIds.Add(reordered.Id);
        InteractionMarker path = reordered.Interactions.OrderBy(m => m.Frame).First();
        Check("branch: Duplicate() carries the row", path.BranchRows?.ContainsKey("path") == true);

        reordered.Interactions.Add(new InteractionMarker { Frame = 7300, Input = InputKind.Button1 });
        string moved = SingeExporter.Export(project2).Script;
        Check("branch: the row is renumbered when the move's position changes",
              moved.Contains("path[2] = {BUTTON1,1039,0,0,0,0,0,0,2}", StringComparison.Ordinal) &&
              !moved.Contains("path[1] =", StringComparison.Ordinal));

        // Losing the row is the crash, so the exporter has to say so.
        path.BranchRows = null;
        Check("branch: a PATH move with no row is reported",
              SingeExporter.Export(project2).Warnings.Any(
                  w => w.Contains("has no path[", StringComparison.Ordinal)));

        // A row pointing at a move that does not exist is the author's bug.
        var orphan = new LdpProject { Name = "Orphan" };
        SingeImporter.Result orphanResult = SingeImporter.Import(orphan, script.Replace(
            "path[1] = {BUTTON1", "path[9] = {BUTTON1", StringComparison.Ordinal));
        Check("branch: a row with no move to belong to is reported",
              orphanResult.Warnings.Any(w => w.Contains("no move 9", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The "badlands" style, from a real community script: the author thinks of
    /// a move as a DEADLINE, so the window is written backwards from the frame
    /// you can see on screen —
    ///
    ///     gap = 10
    ///     move[1] = {5679-gap, 5679, BUTTON1, 2}
    ///
    /// The arithmetic is the mirror of the menu-offset form (number first, name
    /// second), which used to match nothing, so EVERY move in the game was
    /// reported "malformed" and dropped. The Death[] table had the same problem
    /// from the other direction: its frames are counted off a landmark too, and
    /// an unmatched Death[] line looks exactly like a script with no deaths, so
    /// each surviving move then reported its death as missing.
    ///
    /// Death# also takes a sign. The framework reads -1 as "optional move" and
    /// -2 as "optional move with score" — neither is a death, and neither
    /// survives being parsed as an unsigned int.
    /// </summary>
    private static void DeadlineStyleChecks(Action<string, bool> Check)
    {
        const string script = """
            gap = 10
            offsetDeath = 20000

            Death[01] = {offsetDeath+32, offsetDeath+181}
            Death[02] = {offsetDeath+209, offsetDeath+276}

            Level[1] = {"City", 5000, 5001, 1, 0, 0, 0}

            function setupMoves(thisLevel, thisScene)
            if thisLevel == 1 then
                if thisScene == 1 then
                    sceneStart = 5623
                    sceneEnd   = 6086
                    totalMoves = 5
                    move[1] = {5679-gap, 5679, BUTTON1, 2}
                    move[2] = {5826-gap, 5826, BUTTON1, 1}
                    move[3] = {5900+gap, 5950, BUTTON2, 0}
                    move[4] = {6000, 6040, HOLDBUT, -1}
                    move[5] = {6041, 6061, LETGO, -1}
                end
            end
            end
            """;

        var project = new LdpProject { Name = "Badlands" };
        SingeImporter.Result result = SingeImporter.Import(project, script);

        Check("deadline: every move survives the import", result.Moves == 5);
        Check("deadline: nothing reported as malformed",
              !result.Warnings.Any(w => w.Contains("malformed", StringComparison.OrdinalIgnoreCase)));

        List<InteractionMarker> moves = project.Clips
            .SelectMany(c => c.Interactions).OrderBy(m => m.Frame).ToList();

        // 5679-gap = 5669, and the window ENDS on the frame the author named.
        Check("deadline: number-minus-name resolves",
              moves is [{ Frame: 5669 }, { Frame: 5816 }, { Frame: 5910 }, { Frame: 6000 }, { Frame: 6041 }]);
        Check("deadline: the deadline is kept as the window end",
              moves[0].EndFrameOverride == 5679 && moves[1].EndFrameOverride == 5826);
        Check("deadline: number-plus-name resolves too", moves[2].Frame == 5910);

        // The Death[] table is counted off a landmark as well.
        Check("deadline: derived Death[] frames import", result.Deaths == 2);
        Check("deadline: Death[01] resolved through the symbol table",
              project.DeathPool.Count == 2 &&
              project.ClipById(project.DeathPool[0]) is { StartFrame: 20032, EndFrame: 20181 });
        Check("deadline: no move reports a missing death",
              !result.Warnings.Any(w => w.Contains("missing Death", StringComparison.Ordinal)));
        Check("deadline: a move's death resolves to the right scene",
              project.ClipById(moves[0].DeathClipId ?? Guid.Empty) is { StartFrame: 20209 });

        // -1 is not a death and must not become one, nor be dropped.
        Check("deadline: an optional move is kept, not dropped", moves[3].RawDeathIndex == -1);
        Check("deadline: an optional move gets no death and no warning",
              moves[3].DeathClipId == null && !moves[3].RandomDeath);

        // Death# 0 still means what it meant.
        Check("deadline: Death# 0 still round-trips as written", moves[2].RandomDeath);

        // Round trip: the codes go back out exactly as written.
        string exported = SingeExporter.Export(project).Script;
        Check("deadline: the optional code is written back",
              exported.Contains("HOLDBUT, -1}", StringComparison.Ordinal));
        Check("deadline: the resolved frames are written back as numbers",
              exported.Contains("{5669, 5679, BUTTON1,", StringComparison.Ordinal));
        // Specifically the DEATH warning: an optional move is not a move whose
        // death is missing. (Other warnings about the same frame - hold pairing,
        // for one - are a different question and stay.)
        Check("deadline: an optional move is not reported as missing a death",
              !SingeExporter.Export(project).Warnings.Any(
                  w => w.Contains("6000", StringComparison.Ordinal) &&
                       w.Contains("no death scene", StringComparison.Ordinal)));
    }

    private static void OffsetChecks(Action<string, bool> Check)
    {
        // The author's own example, verbatim - note the ragged spacing and the
        // "+0" that a stricter reader would trip on.
        const string script = """
            offsetMenus = 49666

            frameOptions = offsetMenus +0
            frameVictory = offsetMenus +3
            frameSave = offsetMenus +6
            frameRankings =offsetMenus +12
            frameControls = offsetMenus +27
            """;

        LuaValues.Table t = LuaValues.Build(script);
        Check("lua: landmark itself resolves", t.TryGet("offsetMenus", out int menus) && menus == 49666);
        Check("lua: +0 resolves to the landmark", t.TryGet("frameOptions", out int o) && o == 49666);
        Check("lua: +3 resolves", t.TryGet("frameVictory", out int v) && v == 49669);
        Check("lua: +6 resolves", t.TryGet("frameSave", out int s) && s == 49672);
        Check("lua: no space before the name resolves", t.TryGet("frameRankings", out int r) && r == 49678);
        Check("lua: +27 resolves", t.TryGet("frameControls", out int c) && c == 49693);
        Check("lua: nothing left unresolved", t.Unresolved.Count == 0);

        // Subtraction, a bare alias, and a value built on a derived value.
        LuaValues.Table chain = LuaValues.Build("""
            offsetDeath = 1000
            frameA = offsetDeath - 40
            frameB = offsetDeath
            frameC = frameA + 5
            """);
        Check("lua: subtraction resolves", chain.TryGet("frameA", out int a) && a == 960);
        Check("lua: a bare alias resolves", chain.TryGet("frameB", out int b) && b == 1000);
        Check("lua: a chain through a derived value resolves", chain.TryGet("frameC", out int cc) && cc == 965);

        // Order must not matter - Lua would run these top to bottom, but a
        // script may define the landmark after the values built on it.
        LuaValues.Table backwards = LuaValues.Build("frameLate = offsetLate + 7\noffsetLate = 200\n");
        Check("lua: forward reference resolves on a later pass",
              backwards.TryGet("frameLate", out int late) && late == 207);

        // A name the script never defines is reported, not guessed at.
        LuaValues.Table missing = LuaValues.Build("frameGhost = offsetNothing + 5\n");
        Check("lua: unknown base is left unset", !missing.TryGet("frameGhost", out _));
        Check("lua: unknown base is reported", missing.Unresolved.Contains("frameGhost"));

        // A circular definition must terminate rather than spin.
        LuaValues.Table loop = LuaValues.Build("frameX = frameY + 1\nframeY = frameX + 1\n");
        Check("lua: circular definitions terminate and report",
              loop.Unresolved.Count == 2 && !loop.TryGet("frameX", out _));

        // Later assignment wins, and a trailing comment must not break parsing.
        LuaValues.Table over = LuaValues.Build("frameDup = 10\nframeDup = 20  -- moved\n");
        Check("lua: later assignment wins", over.TryGet("frameDup", out int d) && d == 20);
        Check("lua: trailing comment tolerated",
              LuaValues.Build("offsetC = 5 -- landmark\n").TryGet("offsetC", out int cm) && cm == 5);

        // Inline resolution, used for scene bounds.
        Check("lua: inline literal", LuaValues.Resolve("1234", t) == 1234);
        Check("lua: inline expression", LuaValues.Resolve("offsetMenus +12", t) == 49678);
        Check("lua: inline unknown name is null", LuaValues.Resolve("offsetNope + 1", t) == null);

        // A script saved on Windows. CR is neither space nor tab, so before this
        // was handled it sat between the value and the end-of-line anchor and
        // every one of these patterns quietly stopped matching — the failure mode
        // being a script that imports as empty rather than one that errors.
        LuaValues.Table crlf = LuaValues.Build(script.Replace("\n", "\r\n"));
        Check("lua: CRLF script resolves its landmark",
              crlf.TryGet("offsetMenus", out int cm2) && cm2 == 49666);
        Check("lua: CRLF script resolves derived values",
              crlf.TryGet("frameControls", out int cc2) && cc2 == 49693);
        Check("lua: CRLF and LF agree exactly",
              crlf.Values.Count == t.Values.Count &&
              crlf.Values.All(kv => t.TryGet(kv.Key, out int lf) && lf == kv.Value));

        // Old Mac line endings cost nothing extra to cover once normalising.
        LuaValues.Table cr = LuaValues.Build(script.Replace("\n", "\r"));
        Check("lua: lone CR line endings resolve",
              cr.TryGet("frameVictory", out int cv) && cv == 49669);
    }

    private static void IntroChecks(Action<string, bool> Check)
    {
        // The framework's own rule: a gap under 2 means "no intro clip"
        // (main.singe:1909). That is what this app writes, and it must pass.
        Check("intro: start+1 declares no intro", !LevelIntro.DeclaresIntro(41084, 41085));
        Check("intro: start+1 is plausible", LevelIntro.IsPlausible(41084, 41085, 41084));
        Check("intro: an equal pair declares no intro", !LevelIntro.DeclaresIntro(500, 500));
        Check("intro: a gap of 2 does declare one", LevelIntro.DeclaresIntro(500, 502));

        // The author's four real levels. Runaway's 112-frame title card is
        // believable and must survive; the other two swallow gameplay.
        Check("intro: a real title card survives", LevelIntro.IsPlausible(9452, 9564, 9564));
        Check("intro: an intro ending exactly at the first scene survives",
              LevelIntro.IsPlausible(9452, 9564, 9564));
        Check("intro: Yakuza's 2745-frame span is rejected", !LevelIntro.IsPlausible(19030, 21775, 19030));
        Check("intro: Ceremony's 7407-frame span is rejected", !LevelIntro.IsPlausible(26775, 34182, 26775));
        Check("intro: Final Fight is untouched", LevelIntro.IsPlausible(41084, 41085, 41084));

        // Structural, not a length guess: a short intro that still runs into
        // the opening scene is wrong, and a long one that does not is fine.
        Check("intro: a short overlap is still rejected", !LevelIntro.IsPlausible(1000, 1010, 1005));
        Check("intro: a long clean intro is accepted", LevelIntro.IsPlausible(1000, 3000, 3000));

        // With no scenes there is nothing to measure against, so a cap applies.
        Check("intro: no scenes, short intro accepted", LevelIntro.IsPlausible(1000, 1100, null));
        Check("intro: no scenes, huge intro rejected", LevelIntro.IsPlausible(1000, 9000, null) == false);
        Check("intro: no scenes, exactly at the cap accepted",
              LevelIntro.IsPlausible(1000, 1000 + LevelIntro.MaxIntroFramesWithoutScenes, null));

        // Correction collapses to the framework's "no intro" encoding.
        Check("intro: correction is start+1", LevelIntro.Correct(19030, 21775, 19030) == 19031);
        Check("intro: a good intro is returned unchanged", LevelIntro.Correct(9452, 9564, 9564) == 9564);
        Check("intro: correcting twice is stable",
              LevelIntro.Correct(19030, LevelIntro.Correct(19030, 21775, 19030), 19030) == 19031);
        Check("intro: explanation names the level and the overlap",
              LevelIntro.Explain(2, "Yakuza", 19030, 21775, 19030) is { } e &&
              e.Contains("Yakuza") && e.Contains("21775") && e.Contains("19030"));
        Check("intro: nothing to explain for a good level",
              LevelIntro.Explain(1, "Runaway", 9452, 9564, 9564) == null);
    }

    /// <summary>End to end, through the importer, on a script shaped like the real ones.</summary>
    private static void ImportChecks(Action<string, bool> Check)
    {
        const string script = """
            offsetMenus = 49666
            frameOptions = offsetMenus +0
            frameRankings = offsetMenus +12

            Level[1] = {"Runaway", 100, 150, 1, 0, 0, -1}
            Level[2] = {"Yakuza", 5000, 9000, 1, 0, 0, -1}

            function setupMoves(thisLevel, thisScene)

                if thisLevel == 1 then
                    if thisScene == 1 then
                        sceneStart = 150
                        sceneEnd = 400
                        move[n] = {200, 220, UP, 0};n=n+1
                    end
                end

                if thisLevel == 2 then
                    if thisScene == 1 then
                        sceneStart = 5000
                        sceneEnd = 5600
                        move[n] = {5100, 5120, LEFT, 0};n=n+1
                    end
                end

            end
            """;

        var project = new LdpProject { Name = "Fidelity" };
        SingeImporter.Result result = SingeImporter.Import(project, script);

        Check("import: both levels read", result.Levels == 2);
        Check("import: menu offsets resolved into slots",
              project.Slots.Stills.ContainsValue(49666) && project.Slots.Stills.ContainsValue(49678));

        GameLevel runaway = project.Levels[0];
        GameLevel yakuza = project.Levels[1];
        Check("import: a real intro survives", runaway.IntroEndFrame == 150 && runaway.HasIntro);
        Check("import: an intro that swallows gameplay is corrected",
              yakuza.IntroEndFrame == 5001 && !yakuza.HasIntro);
        Check("import: the correction is reported",
              result.Warnings.Any(w => w.Contains("Yakuza") && w.Contains("9000")));
        Check("import: a good level draws no warning",
              !result.Warnings.Any(w => w.Contains("Runaway") && w.Contains("intro clip")));

        // Scene bounds written as arithmetic must land as real frames.
        var offsetProject = new LdpProject { Name = "OffsetScenes" };
        SingeImporter.Result offsetResult = SingeImporter.Import(offsetProject, """
            offsetLevel = 8000

            Level[1] = {"Derived", 8000, 8001, 1, 0, 0, -1}

            function setupMoves(thisLevel, thisScene)
                if thisLevel == 1 then
                    if thisScene == 1 then
                        sceneStart = offsetLevel +40
                        sceneEnd = offsetLevel + 300
                    end
                end
            end
            """);
        Check("import: a derived sceneStart becomes a real frame",
              offsetProject.Clips.Any(c => c.StartFrame == 8040 && c.EndFrame == 8300));
        Check("import: no warning for resolvable scene bounds",
              !offsetResult.Warnings.Any(w => w.Contains("could not be resolved")));


        // The whole importer against a Windows-saved script. This is the check
        // that would have caught the anchor bug: the reference script on disk is
        // LF, so nothing here exercised CRLF until a git checkout converted the
        // test file itself and fourteen checks went red at once.
        var crlfProject = new LdpProject { Name = "CrlfScript" };
        SingeImporter.Result crlfResult = SingeImporter.Import(crlfProject, script.Replace("\n", "\r\n"));
        Check("import: a CRLF script reads both levels", crlfResult.Levels == 2);
        Check("import: a CRLF script reads its scenes and moves",
              crlfProject.Clips.Count == project.Clips.Count &&
              crlfProject.Clips.Sum(c => c.Interactions.Count) == project.Clips.Sum(c => c.Interactions.Count));
        Check("import: a CRLF script resolves its menu offsets",
              crlfProject.Slots.Stills.ContainsValue(49666));
        Check("import: a CRLF script corrects the bogus intro too",
              crlfProject.Levels[1].IntroEndFrame == 5001);
    }
}
