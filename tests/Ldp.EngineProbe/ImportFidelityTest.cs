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

        // RelativeFrames means the two numbers are in different units, so the
        // intro check must stand down rather than "correct" a load-bearing value.
        var relProject = new LdpProject { Name = "Relative" };
        SingeImporter.Result rel = SingeImporter.Import(relProject, """
            RelativeFrames = true

            Level[1] = {"Base", 19030, 21775, 1, 0, 0, -1}

            function setupMoves(thisLevel, thisScene)
                if thisLevel == 1 then
                    if thisScene == 1 then
                        sceneStart = 0
                        sceneEnd = 600
                    end
                end
            end
            """);
        Check("import: RelativeFrames leaves the intro alone",
              relProject.Levels[0].IntroEndFrame == 21775);
        Check("import: RelativeFrames is called out",
              rel.Warnings.Any(w => w.Contains("RelativeFrames")));
    }
}
