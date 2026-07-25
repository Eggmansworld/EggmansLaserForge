using Ldp.Project;
using Rx = System.Text.RegularExpressions.Regex;

namespace Ldp.EngineProbe;

/// <summary>
/// Round-trip and frame-mapping checks for the project model. The mapping
/// cases mirror the real Sonic frame file, including its 1-frame gap at the
/// main/attract boundary, and must behave exactly like Hypseus mpeg_info().
/// </summary>
public static class ProjectTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok) failures++;
        }

        // Model mirroring the Sonic frame file (with its authentic gap).
        var project = new LdpProject { Name = "SonicTest" };
        project.Videos.Add(new VideoSource { Path = "Video/main.m2v", GlobalBase = 0, PictureCount = 96960 });
        project.Videos.Add(new VideoSource { Path = "Video/attract.m2v", GlobalBase = 96961, PictureCount = 3621 });
        project.Videos.Add(new VideoSource { Path = "Video/studios.m2v", GlobalBase = 100582, PictureCount = 1110 });

        Check("global 0 -> main[0]", project.Resolve(0) == (0, 0));
        Check("global 96959 -> main[96959]", project.Resolve(96959) == (0, 96959));
        Check("global 96960 is a dead frame (gap)", project.Resolve(96960) == null);
        Check("global 96961 -> attract[0]", project.Resolve(96961) == (1, 0));
        Check("global 100581 -> attract[3620]", project.Resolve(100581) == (1, 3620));
        Check("global 100582 -> studios[0]", project.Resolve(100582) == (2, 0));
        Check("global past end is invalid", project.Resolve(101692) == null);
        Check("ToGlobal inverts Resolve", project.ToGlobal(1, 3620) == 100581);
        Check("NextGlobalBase is contiguous", project.NextGlobalBase() == 100582 + 1110);

        // A scene whose frames live in a later video must resolve to THAT video,
        // never the first (the double-click-goes-to-video-0 bug). A scene with
        // no stored video index — the whole point — still resolves correctly.
        var studiosScene = new Clip { Name = "SEGA title", StartFrame = 101092, EndFrame = 101317 };
        Check("scene in 3rd video resolves to index 2", project.VideoIndexOf(studiosScene.StartFrame) == 2);
        Check("VideoIndexOf gap returns -1", project.VideoIndexOf(96960) == -1);
        Check("VideoIndexOf past end returns -1", project.VideoIndexOf(999999) == -1);

        // Contiguous default bases for freshly-built projects.
        var fresh = new LdpProject();
        fresh.Videos.Add(new VideoSource { PictureCount = 100, GlobalBase = fresh.NextGlobalBase() });
        fresh.Videos.Add(new VideoSource { PictureCount = 50, GlobalBase = fresh.NextGlobalBase() });
        Check("fresh base[0] == 0", fresh.Videos[0].GlobalBase == 0);
        Check("fresh base[1] == 100", fresh.Videos[1].GlobalBase == 100);
        Check("no gap: global 100 -> video1[0]", fresh.Resolve(100) == (1, 0));

        // Interaction spacing rules: window+cushion at Easy (2 x 20 = 40).
        var scene = new Clip { Name = "scene", StartFrame = 1000, EndFrame = 1200 };
        var ok1 = new InteractionMarker { Frame = 1000, Input = InputKind.Up };
        var ok2 = new InteractionMarker { Frame = 1040, Input = InputKind.Button1 }; // exactly min spacing
        var tooClose = new InteractionMarker { Frame = 1079, Input = InputKind.Down }; // 39 after ok2
        var pastEnd = new InteractionMarker { Frame = 1190, Input = InputKind.Left };  // window ends at 1209 > 1200
        var outside = new InteractionMarker { Frame = 900, Input = InputKind.Right };
        scene.Interactions.AddRange([ok1, ok2, tooClose, pastEnd, outside]);

        HashSet<Guid> violators = InteractionRules.FindViolators(scene, Difficulty.DefaultBaseWindow);
        Check("spacing: 40-frame gap is legal", !violators.Contains(ok2.Id));
        Check("spacing: 39-frame gap flagged", violators.Contains(tooClose.Id));
        Check("window past clip end flagged", violators.Contains(pastEnd.Id));
        Check("marker outside clip flagged", violators.Contains(outside.Id));
        Check("first marker at clip start is legal", !violators.Contains(ok1.Id));
        Check("difficulty windows 20/18/16/12",
              Difficulty.Levels.Select(l => Difficulty.Window(20, l.Offset)).SequenceEqual([20, 18, 16, 12]));

        // Save / load round trip.
        string dir = Path.Combine(Path.GetTempPath(), "ldp-project-test");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "roundtrip.ldproj");
        project.Clips.Add(new Clip
        {
            Name = "Test clip",
            Description = "line1\nline2",
            StartFrame = 96961,
            EndFrame = 97060,
            Interactions = [new InteractionMarker { Frame = 96970, Input = InputKind.Button2, Note = "duck!" }],
        });
        ProjectFile.Save(project, path);
        ProjectFile.Save(project, path); // second save exercises the .bak path
        LdpProject loaded = ProjectFile.Load(path);

        Check("roundtrip name", loaded.Name == "SonicTest");
        Check("roundtrip videos", loaded.Videos.Count == 3 && loaded.Videos[1].GlobalBase == 96961);
        Check("roundtrip clip", loaded.Clips.Count == 1
                                && loaded.Clips[0].StartFrame == 96961
                                && loaded.Clips[0].FrameCount == 100
                                && loaded.Clips[0].Description == "line1\nline2"
                                && loaded.Clips[0].Id == project.Clips[0].Id);
        Check("roundtrip interaction", loaded.Clips[0].Interactions.Count == 1
                                       && loaded.Clips[0].Interactions[0].Frame == 96970
                                       && loaded.Clips[0].Interactions[0].Input == InputKind.Button2
                                       && loaded.Clips[0].Interactions[0].Note == "duck!");
        Check("backup exists", File.Exists(path + ".bak"));

        // Regression: enum defaults (NodeKind.Start = 0) must survive a save/load
        // round trip - a serializer setting once demoted Start nodes to clips.
        var graphProject = new LdpProject { Name = "GraphTest" };
        var startNode = new StoryNode { Kind = NodeKind.Start, X = 10, Y = 20 };
        var clipNode = new StoryNode { Kind = NodeKind.Clip, ClipId = Guid.NewGuid(), X = 300, Y = 20 };
        graphProject.Graph.Nodes.AddRange([startNode, clipNode]);
        graphProject.Graph.Edges.Add(new StoryEdge { FromNode = startNode.Id, FromPort = PortKind.Out, ToNode = clipNode.Id });
        string graphPath = Path.Combine(dir, "graph.ldproj");
        ProjectFile.Save(graphProject, graphPath);
        LdpProject graphLoaded = ProjectFile.Load(graphPath);
        Check("start node Kind survives round trip", graphLoaded.Graph.Start?.Id == startNode.Id);
        Check("edge FromPort=Out survives round trip",
              graphLoaded.Graph.Edges is [{ FromPort: PortKind.Out }]);

        // Healer: a corrupted graph (Start demoted to an empty clip node, plus a
        // stray empty clip node) is repaired instead of crashing the canvas.
        var sick = new StoryGraph();
        var demotedStart = new StoryNode { Kind = NodeKind.Clip, ClipId = null, X = 1, Y = 1 };
        var strayOrphan = new StoryNode { Kind = NodeKind.Clip, ClipId = null, X = 2, Y = 2 };
        var realClip = new StoryNode { Kind = NodeKind.Clip, ClipId = Guid.NewGuid(), X = 3, Y = 3 };
        sick.Nodes.AddRange([strayOrphan, demotedStart, realClip]);
        sick.Edges.Add(new StoryEdge { FromNode = demotedStart.Id, FromPort = PortKind.Out, ToNode = realClip.Id });
        sick.Edges.Add(new StoryEdge { FromNode = realClip.Id, FromPort = PortKind.Out, ToNode = strayOrphan.Id });
        sick.Heal();
        Check("healer restores demoted Start", sick.Start?.Id == demotedStart.Id);
        Check("healer drops stray empty clip nodes", sick.Nodes.All(n => n.Id != strayOrphan.Id));
        Check("healer coerces clip-node Out ports to Success",
              sick.Edges.All(x => sick.NodeById(x.FromNode)!.Kind != NodeKind.Clip || x.FromPort != PortKind.Out));

        // Skip moves: custom window participates in spacing.
        var skipScene = new Clip { Name = "skip", StartFrame = 2000, EndFrame = 5904 };
        var skip = new InteractionMarker { Frame = 2000, Input = InputKind.Skip, EndFrameOverride = 3171 };
        var afterSkipOk = new InteractionMarker { Frame = 3240, Input = InputKind.Down }; // 3171+1+20 = 3192 <= 3240
        skipScene.Interactions.AddRange([skip, afterSkipOk]);
        HashSet<Guid> v1 = InteractionRules.FindViolators(skipScene, 20);
        Check("skip window is legal", !v1.Contains(skip.Id));
        Check("move after skip cushion is legal", !v1.Contains(afterSkipOk.Id));

        // 3180 lands inside the skip's cushion (needs >= 3192) -> flagged.
        var afterSkipBad = new InteractionMarker { Frame = 3180, Input = InputKind.Up };
        var badScene = new Clip { Name = "skip2", StartFrame = 2000, EndFrame = 5904 };
        badScene.Interactions.AddRange([
            new InteractionMarker { Frame = 2000, Input = InputKind.Skip, EndFrameOverride = 3171 },
            afterSkipBad,
        ]);
        HashSet<Guid> v2 = InteractionRules.FindViolators(badScene, 20);
        Check("move inside skip cushion flagged", v2.Contains(afterSkipBad.Id));

        // Game slots (framework non-game elements) round trip.
        var slotProject = new LdpProject { Name = "SlotTest" };
        var titleScene = new Clip { Name = "SEGA logo", StartFrame = 101092, EndFrame = 101317 };
        slotProject.Clips.Add(titleScene);
        slotProject.Slots.Ranges[RangeSlot.Title] = titleScene.Id;
        slotProject.Slots.Stills[StillSlot.Controls] = 101698;
        slotProject.Slots.Stills[StillSlot.DifficultyExtreme] = 105500;
        string slotPath = Path.Combine(dir, "slots.ldproj");
        ProjectFile.Save(slotProject, slotPath);
        LdpProject slotLoaded = ProjectFile.Load(slotPath);
        Check("slots: range assignment survives", slotLoaded.Slots.Ranges[RangeSlot.Title] == titleScene.Id);
        Check("slots: still frames survive",
              slotLoaded.Slots.Stills[StillSlot.Controls] == 101698 &&
              slotLoaded.Slots.Stills[StillSlot.DifficultyExtreme] == 105500);
        Check("slot catalog covers script section 2",
              SlotCatalog.Ranges.Length == 16 && SlotCatalog.Stills.Length == 16);

        // ---- Singe import: both real community scripts ----
        // These reference scripts live in the user's local temp/ reference stash
        // (moved out of the public repo). If absent, skip the import/template
        // integration tests rather than hard-crashing the whole suite.
        string[] sonicPaths =
        [
            @"C:\Eggmansworld\EggmansLaserForge\temp\HypseusSinge\singe\Sonic_the_Hedgehog_1996\Sonic_the_Hedgehog_1996.singe",
            @"C:\Eggmansworld\EggmansLaserdiscPublisher\assets\HypseusSinge\singe\Sonic_the_Hedgehog_1996\Sonic_the_Hedgehog_1996.singe",
        ];
        string? sonicScriptPath = sonicPaths.FirstOrDefault(File.Exists);
        if (sonicScriptPath == null)
        {
            Console.WriteLine("  sonic import: SKIPPED (reference script not found — set aside in temp/)");
            FfmpegCommandTest.Run(Check);
            Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
            return failures == 0 ? 0 : 1;
        }
        string sonicScript = File.ReadAllText(sonicScriptPath);
        var sonicProject = new LdpProject { Name = "SonicImport" };
        SingeImporter.Result sonic = SingeImporter.Import(sonicProject, sonicScript);
        Console.WriteLine($"  sonic import: {sonic.Levels} levels, {sonic.Scenes} scenes, {sonic.Moves} moves, " +
                          $"{sonic.Deaths} deaths, {sonic.SlotsFilled} slots, {sonic.Warnings.Count} warnings");
        Check("sonic: 7 levels", sonic.Levels == 7);
        Check("sonic: 23 deaths", sonic.Deaths == 23);
        Check("sonic: level 1 titled SOUTH ISLAND", sonicProject.Levels[0].Title == "SOUTH ISLAND");
        Check("sonic: L1 has 2 scenes", sonicProject.Levels[0].SceneIds.Count == 2);
        Clip sonicScene1 = sonicProject.Clips.First(c => c.Id == sonicProject.Levels[0].SceneIds[0]);
        Check("sonic: L1S1 range 2000-5904", sonicScene1.StartFrame == 2000 && sonicScene1.EndFrame == 5904);
        Check("sonic: L1S1 has 19 moves", sonicScene1.Interactions.Count == 19);
        InteractionMarker sonicSkip = sonicScene1.Interactions.First();
        Check("sonic: skip 2000-3171 imported", sonicSkip.Input == InputKind.Skip
                                                && sonicSkip.Frame == 2000 && sonicSkip.EndFrameOverride == 3171);
        Check("sonic: title slot assigned", sonicProject.Slots.Ranges.ContainsKey(RangeSlot.Title));
        Check("sonic: storyboard built", sonicProject.Graph.Start != null && sonicProject.Graph.Edges.Count > 10);

        // Cliff Hanger SE is the Standard-Framework reference; skip if the doc
        // isn't present (it lives outside the repo and may be moved).
        string[] cliffPaths =
        [
            @"C:\Eggmansworld\EggmansLaserForge\temp\docs\cliff_se_1080.singe",
            @"C:\Eggmansworld\EggmansLaserdiscPublisher\assets\docs\cliff_se_1080.singe",
            @"D:\Downloads\cliff_se_1080.singe",
        ];
        string? cliffPath = cliffPaths.FirstOrDefault(File.Exists);
        if (cliffPath != null)
        {
            var cliffProject = new LdpProject { Name = "CliffImport" };
            SingeImporter.Result cliff = SingeImporter.Import(cliffProject, File.ReadAllText(cliffPath));
            Console.WriteLine($"  cliff import: {cliff.Levels} levels, {cliff.Scenes} scenes, {cliff.Moves} moves, " +
                              $"{cliff.Deaths} deaths, {cliff.SlotsFilled} slots, {cliff.Warnings.Count} warnings");
            Check("cliff: 7 levels", cliff.Levels == 7);
            Check("cliff: scene counts match Level[] lines",
                  cliffProject.Levels.Select(l => l.SceneIds.Count).SequenceEqual([3, 4, 5, 7, 3, 5, 7]));
            Clip cliffL1S2 = cliffProject.Clips.First(c => c.Id == cliffProject.Levels[0].SceneIds[1]);
            InteractionMarker altMove = cliffL1S2.Interactions.First();
            Check("cliff: alt input parsed (UP+BUTTON1)", altMove.Input == InputKind.Up && altMove.AltInput == InputKind.Button1);
            Check("cliff: WAY token parsed", cliffProject.Clips
                  .SelectMany(c => c.Interactions).Any(m => m.Input == InputKind.AnyDirection));
            Check("cliff: no unknown-token warnings", cliff.Warnings.All(w => !w.Contains("unknown input")));
        }
        else
        {
            Console.WriteLine("  cliff import: SKIPPED (cliff_se_1080.singe not found)");
        }

        // ---- Export and re-import: the game must survive the round trip ----
        SingeExporter.Result export = SingeExporter.Export(sonicProject);
        var reimported = new LdpProject { Name = "SonicRoundTrip" };
        SingeImporter.Result second = SingeImporter.Import(reimported, export.Script);
        Check("roundtrip: level count stable", second.Levels == sonic.Levels);
        Check("roundtrip: move count stable", second.Moves == sonic.Moves);
        Check("roundtrip: scene ranges stable",
              reimported.Levels.SelectMany(l => l.SceneIds)
                  .Select(id => reimported.Clips.First(c => c.Id == id))
                  .Select(c => (c.StartFrame, c.EndFrame))
                  .SequenceEqual(sonicProject.Levels.SelectMany(l => l.SceneIds)
                      .Select(id => sonicProject.Clips.First(c => c.Id == id))
                      .Select(c => (c.StartFrame, c.EndFrame))));
        Check("roundtrip: L1S1 move frames stable",
              reimported.Clips.First(c => c.StartFrame == 2000 && c.EndFrame == 5904).Interactions
                  .Select(m => m.Frame)
                  .SequenceEqual(sonicScene1.Interactions.Select(m => m.Frame)));
        Check("export: frame file lists all videos",
              SingeExporter.BuildFrameFile(slotProject).Contains("Video/"));

        // ---- Template engine: fill a mini community-style template ----
        var tplProject = new LdpProject { Name = "Sonic Test" };
        tplProject.Videos.Add(new VideoSource { Path = "Video/main.m2v", PictureCount = 200000, GlobalBase = 0, Fps = 29.97 });
        var tplTitle = new Clip { Name = "Title", StartFrame = 101092, EndFrame = 101317 };
        var tplPlay = new Clip { Name = "L1 S1", StartFrame = 2000, EndFrame = 5904 };
        var tplDeath = new Clip { Name = "Death: water", StartFrame = 5633, EndFrame = 5689 };
        tplPlay.Interactions.Add(new InteractionMarker { Frame = 3240, Input = InputKind.Down, DeathClipId = tplDeath.Id });
        tplProject.Clips.AddRange([tplTitle, tplPlay, tplDeath]);
        tplProject.Slots.Ranges[RangeSlot.Title] = tplTitle.Id;
        tplProject.Levels.Add(new GameLevel { Title = "SOUTH ISLAND", StartFrame = 1, IntroEndFrame = 1, SceneIds = [tplPlay.Id] });

        string template = string.Join('\n',
        [
            "--@APP-BEGIN readme",
            "old readme to be replaced",
            "--@APP-END readme",
            "singeSetGameName(\"Old Game\")",
            "MYDIR = BASEDIR .. \"/\" .. \"old_game\"",
            "MovieFPS = 23.976\t\t-- keep this helper comment",
            "offsetTitle = 999\t\t\t-- Title start frame helper text",
            "offsetTitleend = 998",
            "ImSounds = true\t\t-- sounds toggle the app does not manage --@APP",
            "-- plain helper comment stays exactly as written",
            "SCORELEVEL = 2000",
            "--@APP-BEGIN deaths",
            "Death[1] = {1, 2}",
            "--@APP-END deaths",
            "finalstage = 9",
            "--@APP-BEGIN levels",
            "Level[1] = {\"OLD\", 1, 2, 3, 0, 0, -1}",
            "--@APP-END levels",
            "--@APP-BEGIN moves",
            "function setupMoves(a, b) end",
            "--@APP-END moves",
        ]);

        SingeTemplate.Result filled = SingeTemplate.Apply(tplProject, template);
        Check("template: game name replaced", filled.Script.Contains("singeSetGameName(\"Sonic Test\")"));
        Check("template: MYDIR sanitized", filled.Script.Contains("MYDIR = BASEDIR .. \"/\" .. \"Sonic_Test\""));
        Check("template: known value substituted, comment kept",
              filled.Script.Contains("offsetTitle = 101092\t\t\t-- Title start frame helper text"));
        Check("template: end value substituted", filled.Script.Contains("offsetTitleend = 101317"));
        Check("template: MovieFPS substituted with comment",
              filled.Script.Contains("MovieFPS = 29.97\t\t-- keep this helper comment"));
        Check("template: unmanaged @APP line untouched + warned",
              filled.Script.Contains("ImSounds = true\t\t-- sounds toggle the app does not manage --@APP") &&
              filled.Warnings.Any(w => w.Contains("ImSounds")));
        Check("template: unmarked unknown line passes through", filled.Script.Contains("SCORELEVEL = 2000"));
        Check("template: helper comment verbatim",
              filled.Script.Contains("-- plain helper comment stays exactly as written"));
        Check("template: deaths block regenerated",
              filled.Script.Contains("Death[1] = {5633, 5689}") && !filled.Script.Contains("Death[1] = {1, 2}"));
        Check("template: levels block regenerated",
              filled.Script.Contains("Level[1] = {\"SOUTH ISLAND\", 1, 2, 1, 0, 0, -1}"));
        Check("template: moves block regenerated",
              filled.Script.Contains("move[1] = {3240, 3260, DOWN, 1}") &&
              !filled.Script.Contains("function setupMoves(a, b) end"));
        Check("template: finalstage substituted", filled.Script.Contains("finalstage = 1"));
        Check("template: markers preserved (re-exportable)",
              filled.Script.Contains("--@APP-BEGIN moves") && filled.Script.Contains("--@APP-END moves"));
        Check("template: readme block generated",
              filled.Script.Contains("PROGRAM NAME:\t\tSonic Test") && !filled.Script.Contains("old readme"));

        // A filled template is itself a valid template: fill it again and the
        // result must be identical (idempotence).
        SingeTemplate.Result refilled = SingeTemplate.Apply(tplProject, filled.Script);
        Check("template: idempotent",
              NormalizeGeneratedDates(refilled.Script) == NormalizeGeneratedDates(filled.Script));

        // ---- The real thing: import Sonic, fill the user's actual template
        // (via the embedded resource - the same bytes every export uses) ----
        string realTemplate = SingeTemplate.DefaultTemplate;
        Check("embedded template loads", realTemplate.Length > 10_000 && realTemplate.Contains("--@APP-BEGIN moves"));
        var realProject = new LdpProject
        {
            Name = "Sonic_the_Hedgehog_1996",
            Framework = GameFramework.StandardFramework,
            Author = "Eggman",
            Synopsis = "Sonic, Tails, and Knuckles battle Dr. Robotnik.",
        };
        realProject.Videos.Add(new VideoSource { Path = "Video/main.m2v", PictureCount = 96960, GlobalBase = 0, Fps = 29.97002997 });
        SingeImporter.Result realImport = SingeImporter.Import(realProject, sonicScript);
        SingeTemplate.Result realFill = SingeTemplate.Apply(realProject, realTemplate);

        Check("real: framework dofile follows project (Framework)",
              realFill.Script.Contains("dofile(BASEDIR .. \"/Framework/globals.singe\")"));
        realProject.Framework = GameFramework.Structure;
        SingeTemplate.Result structFill = SingeTemplate.Apply(realProject, realTemplate);
        // Structure is a custom standalone framework INSIDE the game folder,
        // so its dofile is anchored on MYDIR, not BASEDIR.
        Check("real: Structure dofile uses MYDIR",
              structFill.Script.Contains("dofile(MYDIR .. \"/Structure/globals.singe\")"));
        // The shipped template is deliberately comment-free, but the engine's
        // reason for existing is filling a COMMUNITY-authored script without
        // disturbing a line of it. Prove that against a commented template
        // rather than against our own.
        const string commentedTemplate = """
            -- ------------------------------------
            -- Scoring Settings
            -- ------------------------------------
            SCOREMOVE = 150                 -- Points for a correct move  --@APP
            -- dofile(BASEDIR .. "/FrameworkKimmy/globals.singe")
            dofile(BASEDIR .. "/Framework/globals.singe")
            BASEOVERLAY = OVERLAY_FULL      -- author's own choice
            """;
        string commentedFill = SingeTemplate.Apply(realProject, commentedTemplate).Script;
        Check("template: an author's section headings survive verbatim",
              commentedFill.Contains("-- Scoring Settings") &&
              commentedFill.Contains("-- ------------------------------------"));
        Check("template: an author's trailing comments survive verbatim",
              commentedFill.Contains("-- Points for a correct move") &&
              commentedFill.Contains("-- author's own choice"));
        Check("template: a commented-out dofile is left alone",
              commentedFill.Contains("-- dofile(BASEDIR .. \"/FrameworkKimmy/globals.singe\")"));

        Check("real: scoring passthrough with helper comments",
              structFill.Script.Contains("SCOREMOVE = 150") && structFill.Script.Contains("PERFECTBONUS = 2500"));
        Check("real: scoring override via ScriptValues",
              SingeTemplate.Apply(new LdpProject
              {
                  Name = "X",
                  ScriptValues = { ["SCOREMOVE"] = "225" },
              }, "SCOREMOVE = 150\t-- pts").Script.Contains("SCOREMOVE = 225\t-- pts"));

        Check("real: totalDeath emitted in deaths block", structFill.Script.Contains("totalDeath = 23"));
        Check("real: PlayOrder sized to levels", structFill.Script.Contains("PlayOrder = {1,2,3,4,5,6,7}"));
        Check("real: readme regenerated for project",
              structFill.Script.Contains("PROGRAM NAME:\t\tSonic_the_Hedgehog_1996"));
        Check("real: moves regenerated (spot: L1S1 first real move)",
              structFill.Script.Contains("move[2] = {3240, 3260, DOWN, 1}"));
        Check("real: skip fidelity ({2000, 3171, SKIP, 0})",
              structFill.Script.Contains("move[1] = {2000, 3171, SKIP, 0}"));
        Check("real: explicit Death# 0 preserved without warning",
              structFill.Script.Contains("{46523, 46543, BUTTON1, 0}") &&
              realFill.Warnings.All(w => !w.Contains("46523")));

        // Malformed move (missing Death#) must be surfaced, not silently dropped.
        var malProject = new LdpProject();
        SingeImporter.Result malImport = SingeImporter.Import(malProject,
            "finalstage = 1\nLevel[1] = {\"L\", 1, 2, 1, 0, 0, -1}\n" +
            "function setupMoves(thisLevel, thisScene)\n" +
            "\tif thisLevel == 1 then\n\t\tif thisScene == 1 then\n" +
            "\t\t\tsceneStart = 100\n\t\t\tsceneEnd = 200\n" +
            "\t\t\tmove[n] = {150, 170, LEFT, };n=n+1\n" +
            "\t\tend\n\tend\nend\n");
        Check("malformed move surfaced by importer",
              malImport.Warnings.Any(w => w.Contains("150") && w.Contains("malformed")));
        Check("real: difficulty penalties passthrough",
              structFill.Script.Contains("PenalNormal = 2") && structFill.Script.Contains("PenalExtreme  = 8"));
        Check("real: template fill idempotent",
              NormalizeGeneratedDates(SingeTemplate.Apply(realProject, structFill.Script).Script)
              == NormalizeGeneratedDates(structFill.Script));

        Check("real: README carries AUTHOR and SYNOPSIS",
              structFill.Script.Contains("AUTHOR:\t\t\t\tEggman") &&
              structFill.Script.Contains("SYNOPSIS: Sonic, Tails, and Knuckles battle Dr. Robotnik."));

        // Date field: valid form required, written into the README.
        Check("date validation", LdpProject.IsValidDate("2026-07-14") &&
              !LdpProject.IsValidDate("2026-7-4") && !LdpProject.IsValidDate("07/14/2026") &&
              !LdpProject.IsValidDate(""));
        var datedProject = new LdpProject { Name = "Dated", Author = "A", GameDate = "2026-07-14" };
        SingeExporter.Result dated = SingeExporter.Export(datedProject);
        Check("README carries DATE", dated.Script.Contains("DATE:\t\t\t\t2026-07-14"));
        Check("no DATE warning when valid", dated.Warnings.All(w => !w.Contains("DATE")));
        Check("DATE warning when missing",
              SingeExporter.Export(new LdpProject { Author = "A" }).Warnings.Any(w => w.Contains("DATE is required")));
        Check("real: missing author is a required-field warning",
              SingeTemplate.Apply(new LdpProject { Name = "X" }, "--@APP-BEGIN readme\nx\n--@APP-END readme")
                  .Warnings.Any(w => w.Contains("AUTHOR is required")));
        // Our own template carries none of that: the script it produces is
        // meant to read clean, with the readme block as the only prose.
        Check("real: the shipped template contributes no section headings",
              !structFill.Script.Contains("-- Scoring Settings") &&
              !structFill.Script.Contains("-- Advanced Settings"));
        Check("real: BASEOVERLAY untouched (author's OVERLAY_FULL)",
              structFill.Script.Contains("BASEOVERLAY = OVERLAY_FULL"));
        Check("real: dip_MinimalOverlay and LangOpt present",
              structFill.Script.Contains("dip_MinimalOverlay = 0") &&
              structFill.Script.Contains("{ \"Japanese\", \"_japanese\" }"));
        Check("real: Tiers/life-bar advanced settings present",
              structFill.Script.Contains("Tiers[0] = {4,4,4,5,3}") &&
              structFill.Script.Contains("BarBonus = 3"));

        // ---- Folder/name/FPS/framework additions ----
        Check("SanitizeFolder strips spaces", LdpProject.SanitizeFolder("Sonic Movie 1996!") == "Sonic_Movie_1996");
        Check("SanitizeFolder keeps underscores/dashes", LdpProject.SanitizeFolder("cliff_se-1080") == "cliff_se-1080");
        Check("SanitizeFolder empty falls back", LdpProject.SanitizeFolder("  ") == "MyGame");

        var namedProject = new LdpProject
        {
            Name = "Sonic the Hedgehog, The Movie",
            GameFolder = "Sonic_the_Hedgehog_1996",
            Framework = GameFramework.StandardFramework,
        };
        namedProject.Videos.Add(new VideoSource { Path = "Video/main.m2v", PictureCount = 100, Fps = 29.97002997 });
        // A mini template exercising the three special-cased lines.
        string miniTemplate =
            "singeSetGameName(\"OLD NAME\")\n" +
            "MYDIR = BASEDIR .. \"/\" .. \"old_folder\"\n" +
            "MovieFPS = 23.976\t-- fps\n" +
            "--@APP-BEGIN readme\nx\n--@APP-END readme";
        SingeTemplate.Result named = SingeTemplate.Apply(namedProject, miniTemplate);
        Check("internal Game Name drives singeSetGameName",
              named.Script.Contains("singeSetGameName(\"Sonic the Hedgehog, The Movie\")"));
        Check("Game Folder drives MYDIR (not the internal name)",
              named.Script.Contains("MYDIR = BASEDIR .. \"/\" .. \"Sonic_the_Hedgehog_1996\""));
        Check("internal Game Name drives README title",
              named.Script.Contains("PROGRAM NAME:\t\tSonic the Hedgehog, The Movie"));
        Check("MovieFPS auto-detected from video into script",
              named.Script.Contains("MovieFPS = 29.97"));

        Check("framework display names",
              GameFramework.StandardFramework.Display() == "Framework (global)" &&
              GameFramework.FrameworkKimmy.Display() == "FrameworkKimmy (global)" &&
              GameFramework.Structure.Display() == "Structure (custom standalone)");
        Check("framework picker order is Framework-first",
              GameFrameworkInfo.Ordered.SequenceEqual(
                  [GameFramework.StandardFramework, GameFramework.FrameworkKimmy, GameFramework.Structure]));
        Check("default framework is global Framework", new LdpProject().Framework == GameFramework.StandardFramework);
        Check("only Structure is standalone",
              GameFramework.Structure.IsStandalone() && !GameFramework.StandardFramework.IsStandalone() &&
              !GameFramework.FrameworkKimmy.IsStandalone());

        // Import detects the framework from the dofile line (commented alt ignored).
        var fwImport = new LdpProject();
        SingeImporter.Import(fwImport,
            "-- dofile(BASEDIR .. \"/Framework/globals.singe\")\n" +
            "dofile(MYDIR .. \"/Structure/globals.singe\")\n" +
            "finalstage = 0\nfunction setupMoves(a,b) end");
        Check("import detects Structure framework", fwImport.Framework == GameFramework.Structure);
        var fwImport2 = new LdpProject { Framework = GameFramework.Structure };
        SingeImporter.Import(fwImport2,
            "dofile(BASEDIR .. \"/FrameworkKimmy/globals.singe\")\nfinalstage = 0\nfunction setupMoves(a,b) end");
        Check("import detects Kimmy framework", fwImport2.Framework == GameFramework.FrameworkKimmy);

        // ---- Hypseus launch command ----
        Check("hypseus command shape",
              HypseusLaunch.Command("Sonic_the_Hedgehog_1996") ==
              "hypseus.exe singe vldp -framefile singe\\Sonic_the_Hedgehog_1996\\Sonic_the_Hedgehog_1996.txt " +
              "-script singe\\Sonic_the_Hedgehog_1996\\Sonic_the_Hedgehog_1996.singe " +
              "-fullscreen -linear_scale -volume_nonvldp 40 -volume_vldp 64");

        // ---- Storyboard: play-flow-from-here walks the tail of the chain ----
        var flowProject = new LdpProject();
        var s1 = new Clip { Name = "s1", StartFrame = 100, EndFrame = 200 };
        var s2 = new Clip { Name = "s2", StartFrame = 300, EndFrame = 400 };
        var s3 = new Clip { Name = "s3", StartFrame = 500, EndFrame = 600 };
        flowProject.Clips.AddRange([s1, s2, s3]);
        var start = new StoryNode { Kind = NodeKind.Start };
        var n1 = new StoryNode { Kind = NodeKind.Clip, ClipId = s1.Id };
        var n2 = new StoryNode { Kind = NodeKind.Clip, ClipId = s2.Id };
        var n3 = new StoryNode { Kind = NodeKind.Clip, ClipId = s3.Id };
        flowProject.Graph.Nodes.AddRange([start, n1, n2, n3]);
        flowProject.Graph.Edges.Add(new StoryEdge { FromNode = start.Id, FromPort = PortKind.Out, ToNode = n1.Id });
        flowProject.Graph.Edges.Add(new StoryEdge { FromNode = n1.Id, FromPort = PortKind.Success, ToNode = n2.Id });
        flowProject.Graph.Edges.Add(new StoryEdge { FromNode = n2.Id, FromPort = PortKind.Success, ToNode = n3.Id });
        Check("full flow from Start", flowProject.Graph.SuccessPathClips().SequenceEqual([s1.Id, s2.Id, s3.Id]));
        Check("flow from middle node", flowProject.Graph.SuccessPathFrom(n2).SequenceEqual([s2.Id, s3.Id]));

        // ---- Scoring overrides + template defaults ----
        Check("template default extraction",
              SingeTemplate.ExtractDefaults(realTemplate).TryGetValue("SCOREMOVE", out string? sm) && sm == "150");
        var scoreProject = new LdpProject { ScriptValues = { ["SCOREMOVE"] = "225", ["DEATHPENALTY"] = "300" } };
        string scoreTemplate = "SCOREMOVE = 150\t-- pts\nDEATHPENALTY = 200\t-- pts\nSCORELEVEL = 2000";
        string scored = SingeTemplate.Apply(scoreProject, scoreTemplate).Script;
        Check("scoring override applied", scored.Contains("SCOREMOVE = 225\t-- pts") && scored.Contains("DEATHPENALTY = 300\t-- pts"));
        Check("un-overridden scoring keeps template default", scored.Contains("SCORELEVEL = 2000"));

        // ---- Language tracks (LangOpt block) ----
        var langProject = new LdpProject
        {
            Languages =
            {
                new GameLanguage { Name = "English", Suffix = "" },
                new GameLanguage { Name = "Russian", Suffix = "_russian" },
            },
        };
        string langTemplate = "--@APP-BEGIN langopt\nLangOpt = { { \"Old\", \"\" } }\n--@APP-END langopt";
        string langScript = SingeTemplate.Apply(langProject, langTemplate).Script;
        Check("langopt block regenerated",
              langScript.Contains("{ \"English\", \"\" },") && langScript.Contains("{ \"Russian\", \"_russian\" }") &&
              !langScript.Contains("Old"));
        Check("langopt defaults to English when empty",
              SingeTemplate.Apply(new LdpProject(), langTemplate).Script.Contains("{ \"English\", \"\" }"));

        // Import parses LangOpt back into the project.
        var langImport = new LdpProject();
        SingeImporter.Import(langImport,
            "LangOpt = {\n\t{ \"English\", \"\" },\n\t{ \"Japanese\", \"_japanese\" }\n}\n" +
            "finalstage = 0\nfunction setupMoves(a,b) end");
        Check("langopt imported", langImport.Languages.Count == 2 &&
              langImport.Languages[1].Name == "Japanese" && langImport.Languages[1].Suffix == "_japanese");

        // Round trip: our own template's LangOpt block re-imports cleanly.
        Check("embedded template langopt round trips",
              SingeTemplate.Apply(langProject, realTemplate).Script.Contains("{ \"Russian\", \"_russian\" }"));

        // ---- #4/#5: Level scene counts and totalMoves are auto-derived and
        // internally consistent in the generated script (the engine misbehaves
        // if these disagree with the actual branches / move lines). ----
        string genGame = structFill.Script;
        int sumLevelScenes = Rx.Matches(genGame, @"Level\[\d+\]\s*=\s*\{[^}]*?,\s*\d+\s*,\s*\d+\s*,\s*(\d+)\s*,")
            .Select(m => int.Parse(m.Groups[1].Value)).Sum();
        int sceneBranches = Rx.Matches(genGame, @"thisScene\s*==\s*\d+").Count;
        Check("Level[] scene counts match scene branches", sumLevelScenes == sceneBranches && sceneBranches == 22);
        int totalMovesDecls = Rx.Matches(genGame, @"totalMoves\s*=\s*\d+").Count;
        Check("one totalMoves per scene", totalMovesDecls == sceneBranches);
        int sumTotalMoves = Rx.Matches(genGame, @"totalMoves\s*=\s*(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value)).Sum();
        int moveLines = Rx.Matches(genGame, @"move\[\d+\]\s*=\s*\{").Count;
        Check("totalMoves sum matches emitted move lines", sumTotalMoves == moveLines);

        // Focused: a 2-scene level with 1 move in the first scene, 0 in the second.
        var countProject = new LdpProject { Framework = GameFramework.Structure };
        var cs1 = new Clip { Name = "cs1", StartFrame = 10, EndFrame = 20 };
        cs1.Interactions.Add(new InteractionMarker { Frame = 12, Input = InputKind.Up });
        var cs2 = new Clip { Name = "cs2", StartFrame = 30, EndFrame = 40 };
        countProject.Clips.AddRange([cs1, cs2]);
        countProject.Levels.Add(new GameLevel { Title = "COUNT", StartFrame = 10, IntroEndFrame = 11, SceneIds = [cs1.Id, cs2.Id] });
        string countScript = SingeExporter.Export(countProject).Script;
        Check("level scene count auto = 2", countScript.Contains("Level[1] = {\"COUNT\", 10, 11, 2, 0, 0, -1}"));
        Check("scene 1 totalMoves auto = 1", countScript.Contains("totalMoves = 1"));
        Check("scene 2 totalMoves auto = 0", countScript.Contains("totalMoves = 0"));

        // ---- Level structure: the authoring surface for Level[]/setupMoves ----
        // Levels are never inferred; a scene reaches the exported game only once
        // a level holds it, however it is wired on the storyboard.
        var lvlProject = new LdpProject();
        lvlProject.Videos.Add(new VideoSource { Path = "main.m2v", GlobalBase = 0, PictureCount = 10000 });
        List<Clip> chapters = [];
        for (int i = 0; i < 6; i++)
        {
            var chapter = new Clip { Name = $"Chapter {i + 1:D2}", StartFrame = 1000 + i * 100, EndFrame = 1099 + i * 100 };
            chapters.Add(chapter);
            lvlProject.Clips.Add(chapter);
        }

        Check("levels: a fresh project has none", lvlProject.Levels.Count == 0);
        Check("levels: unassigned scene has no position", lvlProject.LevelPositions().Count == 0);

        GameLevel one = lvlProject.AddLevel("ACT ONE");
        lvlProject.AssignToLevel(one, chapters.Take(3).Select(c => c.Id));
        Check("levels: assign puts 3 scenes in play order",
              one.SceneIds.SequenceEqual(chapters.Take(3).Select(c => c.Id)));
        Check("levels: start follows the first scene", one.StartFrame == 1000 && one.IntroEndFrame == 1001);
        Check("levels: no intro by default", !one.HasIntro);
        Check("levels: position of scene 2 is L1 S2",
              lvlProject.LevelPositions()[chapters[1].Id] == (1, 2));

        // A scene plays in exactly one level: assigning it elsewhere moves it.
        GameLevel two = lvlProject.AddLevel("ACT TWO");
        lvlProject.AssignToLevel(two, [chapters[2].Id]);
        Check("levels: reassign removes from the old level", one.SceneIds.Count == 2);
        Check("levels: reassign adds to the new level", two.SceneIds.SequenceEqual([chapters[2].Id]));
        Check("levels: LevelOf finds the owner", lvlProject.LevelOf(chapters[2].Id) == two);
        Check("levels: LevelOf is null when unassigned", lvlProject.LevelOf(chapters[5].Id) == null);

        // An author-set intro passage owns both frame numbers from then on.
        two.IntroEndFrame = two.StartFrame + 50;
        lvlProject.AssignToLevel(two, [chapters[3].Id]);
        Check("levels: an author intro is not overwritten",
              two.HasIntro && two.StartFrame == 1200 && two.IntroEndFrame == 1250);

        Check("levels: scene reorder moves within the level",
              lvlProject.MoveSceneInLevel(one, chapters[1].Id, -1) &&
              one.SceneIds.SequenceEqual([chapters[1].Id, chapters[0].Id]));
        Check("levels: scene reorder past the edge is refused",
              !lvlProject.MoveSceneInLevel(one, chapters[1].Id, -1));
        Check("levels: reorder resyncs the level start", one.StartFrame == 1100);
        Check("levels: level reorder swaps play order",
              lvlProject.MoveLevel(two, -1) && lvlProject.Levels[0] == two);
        Check("levels: level reorder past the edge is refused", !lvlProject.MoveLevel(two, -1));
        Check("levels: removal unassigns without deleting the scene",
              RemovedCleanly(lvlProject, chapters[0]));

        // Build-from-storyboard walks the success chain only, so deaths — which
        // hang off Death ports — are never swept into a level.
        var chainProject = new LdpProject();
        var chainScenes = new List<Clip>();
        for (int i = 0; i < 4; i++)
        {
            var c = new Clip { Name = $"Scene {i + 1}", StartFrame = 500 + i * 50, EndFrame = 549 + i * 50 };
            chainScenes.Add(c);
            chainProject.Clips.Add(c);
        }
        var death = new Clip { Name = "Death 1", StartFrame = 9000, EndFrame = 9050 };
        chainProject.Clips.Add(death);

        var chainStart = new StoryNode { Kind = NodeKind.Start };
        chainProject.Graph.Nodes.Add(chainStart);
        StoryNode previousNode = chainStart;
        foreach (Clip c in chainScenes)
        {
            var n = new StoryNode { Kind = NodeKind.Clip, ClipId = c.Id };
            chainProject.Graph.Nodes.Add(n);
            chainProject.Graph.Edges.Add(new StoryEdge
            {
                FromNode = previousNode.Id,
                FromPort = previousNode.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success,
                ToNode = n.Id,
            });
            previousNode = n;
        }
        var deathNode = new StoryNode { Kind = NodeKind.Clip, ClipId = death.Id };
        chainProject.Graph.Nodes.Add(deathNode);
        chainProject.Graph.Edges.Add(new StoryEdge
        {
            FromNode = chainProject.Graph.Nodes[1].Id,
            FromPort = PortKind.Death,
            ToNode = deathNode.Id,
        });

        Check("levels: chained scenes start out stranded", chainProject.UnassignedChainScenes().Count == 4);
        GameLevel? built = chainProject.BuildLevelFromStoryboard("LEVEL 1");
        Check("levels: build from storyboard takes the whole chain",
              built != null && built.SceneIds.SequenceEqual(chainScenes.Select(c => c.Id)));
        Check("levels: build from storyboard skips death scenes",
              built != null && !built.SceneIds.Contains(death.Id));
        Check("levels: nothing stranded after building", chainProject.UnassignedChainScenes().Count == 0);
        Check("levels: build on an empty graph returns null",
              new LdpProject().BuildLevelFromStoryboard("X") == null);

        // The stranded-scene warning is what makes a silent export gap visible.
        var strandedProject = new LdpProject { Framework = GameFramework.Structure };
        var strandedScene = new Clip { Name = "Orphan", StartFrame = 10, EndFrame = 20 };
        strandedProject.Clips.Add(strandedScene);
        var sStart = new StoryNode { Kind = NodeKind.Start };
        var sNode = new StoryNode { Kind = NodeKind.Clip, ClipId = strandedScene.Id };
        strandedProject.Graph.Nodes.AddRange([sStart, sNode]);
        strandedProject.Graph.Edges.Add(new StoryEdge { FromNode = sStart.Id, FromPort = PortKind.Out, ToNode = sNode.Id });
        List<string> strandedWarnings = SingeExporter.Export(strandedProject).Warnings;
        Check("levels: no-levels export warns",
              strandedWarnings.Any(w => w.Contains("No levels defined")));
        Check("levels: stranded chain scene is named in a warning",
              strandedWarnings.Any(w => w.Contains("Orphan") && w.Contains("no level")));

        // Copy-paste across levels duplicates the scene rather than stealing it.
        Clip copy = chapters[4].Duplicate();
        Check("levels: duplicate keeps the frame range",
              copy.StartFrame == chapters[4].StartFrame && copy.EndFrame == chapters[4].EndFrame);
        Check("levels: duplicate gets a fresh id", copy.Id != chapters[4].Id);
        Check("levels: duplicate is named as a copy", copy.Name == "Chapter 05 (copy)");

        var dupSource = new Clip { Name = "WithMoves", StartFrame = 1, EndFrame = 100 };
        dupSource.Interactions.Add(new InteractionMarker { Frame = 10, Input = InputKind.Up });
        Clip dupCopy = dupSource.Duplicate();
        dupCopy.Interactions[0].Frame = 99;
        Check("levels: duplicate deep-copies moves",
              dupSource.Interactions[0].Frame == 10 && dupCopy.Interactions[0].Frame == 99);
        Check("levels: duplicated moves get fresh ids",
              dupCopy.Interactions[0].Id != dupSource.Interactions[0].Id);

        // End to end, the reported case: 36 imported chapters chained on the
        // storyboard, the first four carrying moves and death wires, nothing
        // assigned. Before Build-from-Storyboard the script is empty of
        // gameplay; after it, every chapter is a numbered scene of Level 1.
        var film = new LdpProject { Name = "Film", Framework = GameFramework.Structure };
        film.Videos.Add(new VideoSource { Path = "main.m2v", GlobalBase = 0, PictureCount = 200000 });
        var filmStart = new StoryNode { Kind = NodeKind.Start };
        film.Graph.Nodes.Add(filmStart);
        StoryNode filmPrev = filmStart;
        var filmDeath = new Clip { Name = "Death A", StartFrame = 190000, EndFrame = 190100 };
        film.Clips.Add(filmDeath);
        var filmDeathNode = new StoryNode { Kind = NodeKind.Clip, ClipId = filmDeath.Id };
        film.Graph.Nodes.Add(filmDeathNode);

        for (int i = 0; i < 36; i++)
        {
            var chapter = new Clip { Name = $"Chapter {i + 1:D2}", StartFrame = i * 5000, EndFrame = i * 5000 + 4999 };
            if (i < 4) chapter.Interactions.Add(new InteractionMarker
            {
                Frame = chapter.StartFrame + 100,
                Input = InputKind.Up,
                DeathClipId = filmDeath.Id,
            });
            film.Clips.Add(chapter);

            var node = new StoryNode { Kind = NodeKind.Clip, ClipId = chapter.Id };
            film.Graph.Nodes.Add(node);
            film.Graph.Edges.Add(new StoryEdge
            {
                FromNode = filmPrev.Id,
                FromPort = filmPrev.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success,
                ToNode = node.Id,
            });
            if (i < 4) film.Graph.Edges.Add(new StoryEdge
            {
                FromNode = node.Id,
                FromPort = PortKind.Death,
                ToNode = filmDeathNode.Id,
            });
            filmPrev = node;
        }

        string before = SingeExporter.Export(film).Script;
        Check("36-chapter case: unassigned exports finalstage 0", before.Contains("finalstage = 0"));
        Check("36-chapter case: unassigned emits no Level[] line", !before.Contains("Level[1] ="));
        Check("36-chapter case: unassigned setupMoves is empty",
              Rx.IsMatch(before, @"function setupMoves\(thisLevel, thisScene\)\s*end"));
        Check("36-chapter case: all 36 flagged as stranded", film.UnassignedChainScenes().Count == 36);

        GameLevel filmLevel = film.BuildLevelFromStoryboard("LEVEL 1")!;
        string after = SingeExporter.Export(film).Script;
        Check("36-chapter case: build takes all 36 chapters", filmLevel.SceneIds.Count == 36);
        Check("36-chapter case: the death scene stays out of the level",
              !filmLevel.SceneIds.Contains(filmDeath.Id));
        Check("36-chapter case: finalstage becomes 1", after.Contains("finalstage = 1"));
        Check("36-chapter case: Level[1] declares 36 scenes",
              after.Contains("Level[1] = {\"LEVEL 1\", 0, 1, 36, 0, 0, -1}"));
        Check("36-chapter case: setupMoves gets 36 scene branches",
              Rx.Matches(after, @"thisScene == \d+ then").Count == 36);
        Check("36-chapter case: the 4 authored moves are emitted",
              Rx.Matches(after, @"move\[\d+\]\s*=\s*\{").Count == 4);
        Check("36-chapter case: offsetMovieEnd reaches the last chapter",
              after.Contains("offsetMovieEnd = 179999"));
        Check("36-chapter case: nothing stranded, no warnings about levels",
              film.UnassignedChainScenes().Count == 0 &&
              !SingeExporter.Export(film).Warnings.Any(w => w.Contains("no level")));

        Check("levels: replay labels cover the framework values",
              ReplayCatalog.Display(-1).StartsWith("Replay until") &&
              ReplayCatalog.Display(0).StartsWith("Skip") &&
              ReplayCatalog.Display(4) == "Requeue at scene 4");

        // ---- Level scenes the framework cannot survive ----
        // setupLevel does `move = nil; move = {}` then calls setupMoves, so a
        // scene with no moves leaves the table empty; main.singe:1941 then does
        // move[currentMove-1][inputFrmEnd] and dies with "attempt to index field
        // '?' (a nil value)". The scene can never complete either, since levels
        // advance by finishing moves. No published working game has one:
        // Sonic's lowest scene is 7 moves. Frame 0 is separately unusable - it
        // is the framework's "not set" sentinel and its seek guard
        // (`currentFrame + 2 <= sceneStart`) can never be true for it.
        var badScenes = new LdpProject { Framework = GameFramework.Structure };
        var atZero = new Clip { Name = "Chapter 01", StartFrame = 0, EndFrame = 5003 };
        atZero.Interactions.Add(new InteractionMarker { Frame = 1716, Input = InputKind.Right, ExplicitNoDeath = true });
        var silent = new Clip { Name = "Chapter 05", StartFrame = 18862, EndFrame = 21733 };
        var withSkip = new Clip { Name = "Chapter 06", StartFrame = 21734, EndFrame = 26002 };
        withSkip.Interactions.Add(new InteractionMarker
        {
            Frame = 21800,
            Input = InputKind.Skip,
            EndFrameOverride = 25900,
        });
        badScenes.Clips.AddRange([atZero, silent, withSkip]);
        badScenes.AssignToLevel(badScenes.AddLevel("LEVEL 1"), [atZero.Id, silent.Id, withSkip.Id]);

        List<string> sceneWarnings = SingeExporter.Export(badScenes).Warnings;
        Check("scenes: a level scene with no moves warns by name",
              sceneWarnings.Any(w => w.Contains("Chapter 05") && w.Contains("no moves")));
        Check("scenes: the no-moves warning names its level and scene number",
              sceneWarnings.Any(w => w.Contains("Level 1 scene 2") && w.Contains("no moves")));
        Check("scenes: a scene carrying only a Skip move is accepted",
              !sceneWarnings.Any(w => w.Contains("Chapter 06") && w.Contains("no moves")));
        Check("scenes: a scene starting at global frame 0 warns",
              sceneWarnings.Any(w => w.Contains("Chapter 01") && w.Contains("frame 0")));
        Check("scenes: a scene starting past frame 0 does not warn about it",
              !sceneWarnings.Any(w => w.Contains("Chapter 05") && w.Contains("frame 0")));

        // The script still exports - these are warnings, not a refusal - and the
        // empty scene is still emitted so the Level[] scene count stays honest.
        string badScript = SingeExporter.Export(badScenes).Script;
        Check("scenes: an empty scene still emits totalMoves = 0",
              badScript.Contains("totalMoves = 0"));
        Check("scenes: Level[1] still counts all three scenes",
              badScript.Contains("Level[1] = {\"LEVEL 1\", 0, 1, 3, 0, 0, -1}"));

        // ---- Generated Lua must parse, whatever shape the levels are in ----
        // An empty level opens no `if thisScene` chain, so writing its closing
        // `end` anyway closed the LEVEL branch instead and Lua died on the next
        // `elseif`: "'end' expected (to close 'function' at line N) near
        // 'elseif'". Levels empty out in normal use (pulling scenes back out to
        // re-plan), so every arrangement below has to produce parsable Lua.
        var shapes = new (string Name, int[] SceneCounts)[]
        {
            ("empty level in the middle", [2, 0, 2]),
            ("empty level first", [0, 2]),
            ("empty level last", [2, 0]),
            ("two empty levels after a full one", [4, 0, 0]), // the reported case
            ("every level empty", [0, 0, 0]),
            ("single empty level", [0]),
            ("no empty levels", [2, 3]),
        };
        foreach ((string shapeName, int[] counts) in shapes)
        {
            var shaped = new LdpProject { Framework = GameFramework.Structure };
            shaped.Videos.Add(new VideoSource { Path = "m.m2v", GlobalBase = 0, PictureCount = 200000 });
            int frame = 1000;
            foreach (int count in counts)
            {
                GameLevel shapedLevel = shaped.AddLevel();
                for (int s = 0; s < count; s++)
                {
                    var shapedScene = new Clip { Name = $"S{frame}", StartFrame = frame, EndFrame = frame + 499 };
                    shapedScene.Interactions.Add(new InteractionMarker
                    {
                        Frame = frame + 50,
                        Input = InputKind.Up,
                        ExplicitNoDeath = true,
                    });
                    shaped.Clips.Add(shapedScene);
                    shaped.AssignToLevel(shapedLevel, [shapedScene.Id]);
                    frame += 500;
                }
            }

            string shapedScript = SingeExporter.Export(shaped).Script;
            (int depth, bool negative) = MovesBlockBalance(shapedScript);
            Check($"lua: {shapeName} — setupMoves blocks balance", depth == 0);
            Check($"lua: {shapeName} — no stray end", !negative);
        }

        // An empty level is still a runtime crash even once the Lua parses, so
        // it has to be named rather than quietly emitted.
        var emptyLevelProject = new LdpProject { Framework = GameFramework.Structure };
        emptyLevelProject.AddLevel("GHOST TOWN");
        Check("lua: an empty level warns by name",
              SingeExporter.Export(emptyLevelProject).Warnings
                  .Any(w => w.Contains("GHOST TOWN") && w.Contains("no scenes")));

        // Editing a scene's boundary has to pull its level's start along — the
        // point of the frame-edit flow is nudging an imported chapter off frame
        // 0 without recreating it and losing the moves already authored in it.
        var editProject = new LdpProject { Framework = GameFramework.Structure };
        editProject.Videos.Add(new VideoSource { Path = "m.m2v", GlobalBase = 0, PictureCount = 10000 });
        var edited = new Clip { Name = "Chapter 01", StartFrame = 0, EndFrame = 5003 };
        edited.Interactions.Add(new InteractionMarker { Frame = 1716, Input = InputKind.Right, ExplicitNoDeath = true });
        editProject.Clips.Add(edited);
        GameLevel editLevel = editProject.AddLevel("LEVEL 1");
        editProject.AssignToLevel(editLevel, [edited.Id]);
        Check("edit: the level starts where its scene does", editLevel.StartFrame == 0);
        Check("edit: frame 0 warns before the nudge",
              SingeExporter.Export(editProject).Warnings.Any(w => w.Contains("frame 0")));

        edited.StartFrame = 1;
        editProject.SyncLevelStart(editLevel);
        Check("edit: nudging the scene off 0 pulls the level start with it",
              editLevel.StartFrame == 1 && editLevel.IntroEndFrame == 2);
        Check("edit: the frame-0 warning clears once nudged",
              !SingeExporter.Export(editProject).Warnings.Any(w => w.Contains("frame 0")));
        Check("edit: the move authored inside it survives untouched",
              edited.Interactions.Count == 1 && edited.Interactions[0].Frame == 1716);

        // ---- Framework-driven script values ----
        // The pre-game difficulty screen is FrameworkKimmy's; under Framework
        // the player chooses from the in-game options menu, so leaving it on
        // sends the game to a screen that is not part of its flow.
        foreach ((GameFramework fw, string want) in new[]
                 {
                     (GameFramework.FrameworkKimmy, "true"),
                     (GameFramework.StandardFramework, "false"),
                     (GameFramework.Structure, "false"),
                 })
        {
            string diffScript = SingeTemplate.Apply(
                new LdpProject { Framework = fw }, SingeTemplate.DefaultTemplate).Script;
            Check($"script: IngameDiffchoice = {want} for {fw}",
                  Rx.IsMatch(diffScript, $@"IngameDiffchoice\s*=\s*{want}\b"));
        }

        // A level's death behaviour drives LvlOrder requeue arithmetic, so
        // anything but the loop default has to be called out.
        var replayProject = new LdpProject { Framework = GameFramework.Structure };
        var replayScene = new Clip { Name = "S", StartFrame = 10, EndFrame = 99 };
        replayScene.Interactions.Add(new InteractionMarker { Frame = 20, Input = InputKind.Skip, EndFrameOverride = 90 });
        replayProject.Clips.Add(replayScene);
        GameLevel replayLevel = replayProject.AddLevel("L");
        replayProject.AssignToLevel(replayLevel, [replayScene.Id]);
        Check("script: a new level defaults to replay-until-passed",
              replayLevel.Replay == GameLevel.DefaultReplay && replayLevel.Replay == -1);
        Check("script: the default death behaviour is silent",
              !SingeExporter.Export(replayProject).Warnings.Any(w => w.Contains("death behaviour")));
        replayLevel.Replay = 0;
        Check("script: a non-default death behaviour warns",
              SingeExporter.Export(replayProject).Warnings.Any(w => w.Contains("death behaviour")));
        replayLevel.Replay = GameLevel.DefaultReplay;

        // The template carries no commentary beyond the markers the exporter
        // needs and the --@APP flags that mark author-supplied values.
        string bareTemplate = SingeTemplate.DefaultTemplate;
        List<string> strayComments = bareTemplate.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("--", StringComparison.Ordinal))
            .Where(l => !l.Contains("@APP", StringComparison.Ordinal))
            .ToList();
        Check("script: the template carries no comments but its @APP markers",
              strayComments.Count == 0);
        Check("script: the generated readme points back at the app",
              SingeExporter.Export(replayProject).Script.Contains("github.com/Eggmansworld/EggmansLaserForge"));

        // ---- Support files copied into a new game folder ----
        // A game folder without Cfg/game.cfg cannot boot, so the app lays down a
        // generic set. The picks are swappable by design, which makes "never
        // overwrite" the invariant that protects an author's substitutions.
        string sfRoot = Path.Combine(Path.GetTempPath(), "ldp-support-test");
        if (Directory.Exists(sfRoot)) Directory.Delete(sfRoot, recursive: true);
        string sfSource = Path.Combine(sfRoot, "bundled");
        string sfGame = Path.Combine(sfRoot, "game");
        Directory.CreateDirectory(Path.Combine(sfSource, "Cfg"));
        Directory.CreateDirectory(Path.Combine(sfSource, "Overlay", "Lores"));
        File.WriteAllText(Path.Combine(sfSource, "Cfg", "game.cfg"), "dip_StartLevel = 1\n");
        File.WriteAllText(Path.Combine(sfSource, "Cfg", "default.cfg"), "dip_StartLevel = 1\n");
        File.WriteAllText(Path.Combine(sfSource, "Overlay", "Lores", "skip.png"), "stock");

        List<string> sfAdded = SupportFiles.InstallFrom(sfSource, sfGame);
        Check("support: a fresh game folder gets every file", sfAdded.Count == 3);
        Check("support: the folder layout is preserved",
              File.Exists(Path.Combine(sfGame, "Overlay", "Lores", "skip.png")));
        Check("support: the summary names the areas",
              SupportFiles.Describe(sfAdded).Contains("Cfg") && SupportFiles.Describe(sfAdded).Contains("Overlay"));

        // An author swaps an overlay for their own art; re-running must not
        // stamp the stock file back over it.
        File.WriteAllText(Path.Combine(sfGame, "Overlay", "Lores", "skip.png"), "AUTHOR ART");
        File.Delete(Path.Combine(sfGame, "Cfg", "default.cfg"));
        List<string> sfSecond = SupportFiles.InstallFrom(sfSource, sfGame);
        Check("support: only the genuinely missing file is restored",
              sfSecond.Count == 1 && sfSecond[0].EndsWith("default.cfg", StringComparison.Ordinal));
        Check("support: the author's replacement survives",
              File.ReadAllText(Path.Combine(sfGame, "Overlay", "Lores", "skip.png")) == "AUTHOR ART");
        Check("support: a fully-stocked folder is left alone",
              SupportFiles.InstallFrom(sfSource, sfGame).Count == 0);
        Check("support: an absent bundle is a no-op, not a crash",
              SupportFiles.InstallFrom(Path.Combine(sfRoot, "nope"), sfGame).Count == 0);
        Check("support: nothing added describes as empty", SupportFiles.Describe([]) == "");
        Directory.Delete(sfRoot, recursive: true);

        // ---- Singe's persisted service-menu settings ----
        // Cfg/game.cfg outlives every change made in the app. A start level the
        // project no longer has sends the framework into Level[n] = nil at
        // main.singe:6600, with nothing tying the crash back to a dip switch
        // set days earlier — so the app has to spot it at export time.
        var cfgProject = new LdpProject();
        var cfgScene = new Clip { Name = "S1", StartFrame = 100, EndFrame = 200 };
        cfgProject.Clips.Add(cfgScene);
        cfgProject.AssignToLevel(cfgProject.AddLevel("ONLY LEVEL"), [cfgScene.Id]);

        const string liveCfg = "dip_GameType = 0\ndip_PlayStyle = 0\ndip_StartLevel = 3\ndip_StartScene = 1\n";
        Check("cfg: reads a numeric setting", GameConfig.Value(liveCfg, "dip_StartLevel") == 3);
        Check("cfg: an absent setting reads null", GameConfig.Value(liveCfg, "dip_Nonsense") == null);
        Check("cfg: a start level past the last one warns",
              GameConfig.Validate(liveCfg, cfgProject).Any(w => w.Contains("level 3") && w.Contains("1 level")));
        Check("cfg: an in-range start level is silent",
              GameConfig.Validate("dip_StartLevel = 1\ndip_StartScene = 1\n", cfgProject).Count == 0);
        Check("cfg: an empty file leaves the dips at their defaults",
              GameConfig.Validate("", cfgProject).Count == 0);
        // Deleting game.cfg is the wrong remedy — the framework reads it at boot
        // with no existence check — so the warning has to name the edit AND say
        // not to delete it.
        Check("cfg: the remedy names the edit and warns against deleting",
              GameConfig.Validate(liveCfg, cfgProject)
                  .All(w => w.Contains("edit dip_StartLevel") && w.Contains("Do not delete")));

        // A missing game.cfg is a boot failure, not an unconfigured state:
        // readConfig() does a bare io.input on it with no existence check.
        string cfgDir = Path.Combine(Path.GetTempPath(), "ldp-cfg-test", "Cfg");
        Directory.CreateDirectory(cfgDir);
        string cfgHome = Path.GetDirectoryName(cfgDir)!;
        File.Delete(GameConfig.PathFor(cfgHome));
        File.WriteAllText(Path.Combine(cfgDir, "default.cfg"), "dip_StartLevel = 1\n");
        Check("cfg: a missing game.cfg is reported as a boot failure",
              GameConfig.ValidateFolder(cfgHome, cfgProject)
                  .Any(w => w.Contains("missing") && w.Contains("Copy Cfg/default.cfg")));

        File.WriteAllText(GameConfig.PathFor(cfgHome), "dip_StartLevel = 1\ndip_StartScene = 1\n");
        Check("cfg: a present, in-range game.cfg is silent",
              GameConfig.ValidateFolder(cfgHome, cfgProject).Count == 0);
        Directory.Delete(Path.GetDirectoryName(cfgDir)!, recursive: true);
        Check("cfg: a start scene past the level's last warns",
              GameConfig.Validate("dip_StartLevel = 1\ndip_StartScene = 4\n", cfgProject)
                  .Any(w => w.Contains("scene 4")));
        Check("cfg: zero and negative start levels warn",
              GameConfig.Validate("dip_StartLevel = 0\n", cfgProject).Count == 1);
        Check("cfg: the path sits under the game folder's Cfg",
              GameConfig.PathFor("X").Replace('\\', '/') == "X/Cfg/game.cfg");

        // ---- Attract slots the framework never zero-guards ----
        // doIntro() shows frameSpecial second, guarded only by
        // `frameSpecial ~= frameControls` — which zero passes — then plays
        // offsetIntro02/03 outright, and doFillerFrame() rotates those two
        // between levels. Identical in Framework and FrameworkKimmy. A zero in
        // any of them seeks to frame 0 and freezes the picture while the script
        // runs on, so the app has to demand them rather than default them away.
        Check("slots: frameSpecial is required",
              SlotCatalog.Stills.First(s => s.LuaName == "frameSpecial").Required);
        Check("slots: attract videos 2 and 3 are required",
              SlotCatalog.Ranges.Where(r => r.LuaName is "offsetIntro02" or "offsetIntro03").All(r => r.Required));
        Check("slots: game intro stays optional (framework guards it with ~= 0)",
              !SlotCatalog.Ranges.First(r => r.LuaName == "offsetIntroGame").Required);
        Check("slots: trophies stay optional (guarded by LvlTrophy3 ~= 0)",
              !SlotCatalog.Stills.First(s => s.LuaName == "frameTrophy").Required);

        var attract = new LdpProject { Framework = GameFramework.Structure };
        List<string> attractWarnings = SingeExporter.Export(attract).Warnings;
        Check("slots: unset frameSpecial warns by name",
              attractWarnings.Any(w => w.Contains("frameSpecial")));
        Check("slots: unset attract videos warn by name",
              attractWarnings.Any(w => w.Contains("offsetIntro02")) &&
              attractWarnings.Any(w => w.Contains("offsetIntro03")));

        // Pointing it at the instructions frame is the framework's own way of
        // skipping the step, so that has to count as satisfying the slot.
        attract.Slots.Stills[StillSlot.Controls] = 5000;
        attract.Slots.Stills[StillSlot.SpecialMoves] = 5000;
        Check("slots: frameSpecial reusing frameControls satisfies it",
              !SingeExporter.Export(attract).Warnings.Any(w => w.Contains("frameSpecial")));

        // ---- Video conversion (FFmpeg command builder) ----
        FfmpegCommandTest.Run(Check);

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Block balance of generated Lua from `function setupMoves` onwards: every
    /// `function` and `if ... then` must be closed by exactly one `end`, and the
    /// running depth must never dip below zero. Depth is what catches a stray
    /// `end` at its source — Lua itself only notices lines later, reporting
    /// "'end' expected (to close 'function') near 'elseif'".
    /// </summary>
    private static (int Depth, bool WentNegative) MovesBlockBalance(string script)
    {
        int start = script.IndexOf("function setupMoves", StringComparison.Ordinal);
        if (start < 0) return (int.MinValue, false);

        int depth = 0;
        bool negative = false;
        foreach (string raw in script[start..].Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("--", StringComparison.Ordinal)) continue;
            // `elseif` is one word, so \bif\b never matches inside it.
            if (Rx.IsMatch(line, @"^function\b") || Rx.IsMatch(line, @"^if\b.*\bthen\b")) depth++;
            if (Rx.IsMatch(line, @"^end\b"))
            {
                depth--;
                if (depth < 0) negative = true;
            }
        }
        return (depth, negative);
    }

    /// <summary>Removing a scene from its level must leave the scene itself in
    /// the project — the level list is structure, not ownership.</summary>
    private static bool RemovedCleanly(LdpProject project, Clip scene)
    {
        project.RemoveFromLevels([scene.Id]);
        return project.LevelOf(scene.Id) == null && project.Clips.Contains(scene);
    }

    private static string NormalizeGeneratedDates(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d{2}-\d{2}", "DATE");
}
