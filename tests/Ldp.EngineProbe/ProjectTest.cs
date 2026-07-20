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
        Check("real: commented Kimmy dofile untouched",
              structFill.Script.Contains("-- dofile(BASEDIR .. \"/FrameworkKimmy/globals.singe\")"));

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
        Check("real: section headings survive verbatim",
              structFill.Script.Contains("-- Scoring Settings") &&
              structFill.Script.Contains("-- Advanced Settings") &&
              structFill.Script.Contains("-- Difficulty Settings") &&
              structFill.Script.Contains("-- 1. General settings --"));
        Check("real: moves reference comment block survives",
              structFill.Script.Contains("--\t\tSKIP (skip long non-interactive parts of a video)"));
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

        // ---- Video conversion (FFmpeg command builder) ----
        FfmpegCommandTest.Run(Check);

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 1;
    }

    private static string NormalizeGeneratedDates(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d{2}-\d{2}", "DATE");
}
