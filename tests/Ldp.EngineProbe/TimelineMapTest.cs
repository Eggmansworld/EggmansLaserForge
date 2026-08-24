using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// What a video is and isn't used for.
///
/// The strip above the transport slider used to draw the selected scene alone,
/// so a 100,000-frame video showed one short band in a wide empty bar — and the
/// empty space meant nothing, because it looked the same whether it held the
/// rest of the game or footage no part of the game touches.
///
/// The arithmetic lives in the project layer rather than in a paint method
/// because the same answer drives a future "blank the leftover video" tool, and
/// a tool that overwrites frames must not be reading its ranges off something
/// that was only ever meant to be looked at.
/// </summary>
public static class TimelineMapTest
{
    public static void Run(Action<string, bool> Check)
    {
        RoleChecks(Check);
        CoverageChecks(Check);
        MultiVideoChecks(Check);
    }

    /// <summary>Builds a project with one 10,000-frame video and no content.</summary>
    private static LdpProject NewProject(int pictureCount = 10_000, int globalBase = 0)
    {
        var project = new LdpProject { Name = "Timeline" };
        project.Videos.Add(new VideoSource
        {
            Path = "Video/main.m2v",
            GlobalBase = globalBase,
            PictureCount = pictureCount,
            Fps = 29.97,
        });
        return project;
    }

    private static Clip AddScene(LdpProject project, string name, int start, int end)
    {
        var clip = new Clip { Name = name, StartFrame = start, EndFrame = end };
        project.Clips.Add(clip);
        return clip;
    }

    private static void RoleChecks(Action<string, bool> Check)
    {
        LdpProject project = NewProject();

        Clip play = AddScene(project, "Opening", 1000, 1999);
        Clip death = AddScene(project, "Falls", 3000, 3099);
        Clip slot = AddScene(project, "Attract", 5000, 5499);
        Clip loose = AddScene(project, "Marked but never used", 7000, 7499);

        var level = new GameLevel { Title = "One", StartFrame = 900, IntroEndFrame = 999 };
        level.SceneIds.Add(play.Id);
        project.Levels.Add(level);
        project.DeathPool.Add(death.Id);
        project.Slots.Ranges[RangeSlot.Title] = slot.Id;
        project.Slots.Stills[StillSlot.Victory] = 8000;

        TimelineMap map = TimelineMap.Build(project, 0);

        TimelineRole? RoleOf(Guid id) =>
            map.Spans.FirstOrDefault(s => s.ClipId == id)?.Role;

        Check("timeline: a level's scene is gameplay", RoleOf(play.Id) == TimelineRole.Gameplay);
        Check("timeline: a pooled scene is a death", RoleOf(death.Id) == TimelineRole.Death);
        Check("timeline: a slot scene is a slot", RoleOf(slot.Id) == TimelineRole.Slot);
        // The one the whole feature exists to surface: marked, then forgotten.
        Check("timeline: a scene nothing references is unassigned",
              RoleOf(loose.Id) == TimelineRole.Unassigned);
        Check("timeline: a declared level intro is its own span",
              map.Spans.Any(s => s is { Role: TimelineRole.LevelIntro, StartFrame: 900, EndFrame: 999 }));
        Check("timeline: a still slot is a single frame",
              map.Spans.Any(s => s is { Role: TimelineRole.Still, StartFrame: 8000, EndFrame: 8000 }));

        // A gameplay scene named as a death is a death: one block per scene reads
        // far better than stacked translucent bands, so the most specific job wins.
        var both = new GameLevel { Title = "Two", StartFrame = 1, IntroEndFrame = 1 };
        both.SceneIds.Add(death.Id);
        project.Levels.Add(both);
        Check("timeline: death beats gameplay when a scene is both",
              TimelineMap.Build(project, 0).Spans.First(s => s.ClipId == death.Id).Role == TimelineRole.Death);

        // The framework reads a gap under two frames as "no intro clip", so one
        // must not be drawn — it would claim used footage that never plays.
        var noIntro = new LdpProject { Name = "NoIntro" };
        noIntro.Videos.Add(new VideoSource { PictureCount = 5000, GlobalBase = 0, Fps = 25 });
        noIntro.Levels.Add(new GameLevel { Title = "Bare", StartFrame = 100, IntroEndFrame = 101 });
        Check("timeline: the framework's no-intro encoding draws no intro",
              !TimelineMap.Build(noIntro, 0).Spans.Any(s => s.Role == TimelineRole.LevelIntro));

        // Naming: the scene label carries its level and scene number, which is
        // what the readout under the playhead needs to say.
        Check("timeline: a gameplay span names its level and scene position",
              map.Spans.First(s => s.ClipId == play.Id).Name.StartsWith("L1 S1"));
        Check("timeline: a slot span is named for the slot, not the scene",
              map.Spans.First(s => s.ClipId == slot.Id).Name == "Title video");
    }

    private static void CoverageChecks(Action<string, bool> Check)
    {
        LdpProject project = NewProject(pictureCount: 10_000);
        var level = new GameLevel { Title = "One", StartFrame = 1, IntroEndFrame = 1 };
        level.SceneIds.Add(AddScene(project, "A", 1000, 1999).Id);   // 1000 frames
        level.SceneIds.Add(AddScene(project, "B", 2000, 2999).Id);   // 1000, adjacent
        level.SceneIds.Add(AddScene(project, "C", 5000, 5999).Id);   // 1000, after a gap
        project.Levels.Add(level);

        TimelineMap map = TimelineMap.Build(project, 0);
        Check("timeline: total is the video's picture count", map.TotalFrames == 10_000);
        Check("timeline: used counts every covered frame", map.UsedFrames == 3000);
        Check("timeline: spare is the rest", map.UnusedFrames == 7000);
        Check("timeline: the fraction matches", Math.Abs(map.UsedFraction - 0.30) < 1e-9);

        // Leading gap, the gap between B and C, and the trailing run to the end.
        Check("timeline: unused runs are the exact complement",
              map.UnusedRuns.SequenceEqual([(0, 999), (3000, 4999), (6000, 9999)]));
        Check("timeline: adjacent scenes leave no gap between them",
              !map.UnusedRuns.Any(r => r.StartFrame is >= 1000 and <= 2999));

        // Overlap is a union, not a sum. Two scenes over the same footage - a
        // death reused by two levels, a slot inside a level's video - would
        // otherwise report more used frames than the video has.
        LdpProject overlap = NewProject(pictureCount: 1000);
        var lvl = new GameLevel { Title = "Over", StartFrame = 1, IntroEndFrame = 1 };
        lvl.SceneIds.Add(AddScene(overlap, "Wide", 100, 599).Id);
        lvl.SceneIds.Add(AddScene(overlap, "Inside", 200, 299).Id);
        lvl.SceneIds.Add(AddScene(overlap, "Straddles", 500, 699).Id);
        overlap.Levels.Add(lvl);
        TimelineMap om = TimelineMap.Build(overlap, 0);
        Check("timeline: overlapping scenes are counted once", om.UsedFrames == 600); // 100..699
        Check("timeline: overlap merges into one run",
              om.UnusedRuns.SequenceEqual([(0, 99), (700, 999)]));

        // The readout under the playhead: the most specific span wins, so
        // standing inside "Inside" reports it and not the scene enclosing it.
        Check("timeline: the innermost span describes a frame",
              om.SpanAt(250)?.Name.Contains("Inside") == true);
        Check("timeline: a frame outside every scene has no span", om.SpanAt(50) == null);
        Check("timeline: the level band spans its scenes end to end",
              om.LevelAt(650) is { Number: 1 } && om.LevelAt(50) == null);

        // A video the game covers completely has nothing spare, and one nothing
        // touches is entirely spare. Both are the ends the strip has to draw.
        LdpProject full = NewProject(pictureCount: 500);
        var allLevel = new GameLevel { Title = "All", StartFrame = 1, IntroEndFrame = 1 };
        allLevel.SceneIds.Add(AddScene(full, "Everything", 0, 499).Id);
        full.Levels.Add(allLevel);
        TimelineMap fm = TimelineMap.Build(full, 0);
        Check("timeline: a fully used video has no spare runs",
              fm.UnusedRuns.Count == 0 && fm.UnusedFrames == 0 && fm.UsedFraction == 1.0);

        TimelineMap empty = TimelineMap.Build(NewProject(pictureCount: 500), 0);
        Check("timeline: an untouched video is one spare run",
              empty.UnusedRuns.SequenceEqual([(0, 499)]) && empty.UsedFrames == 0);

        // Short gaps between scenes are rounding, not spare footage. A blanking
        // tool acting on them would save nothing and risk clipping a scene edge.
        Check("timeline: trivial gaps are filtered out of the significant runs",
              map.SignificantUnusedRuns(minimumFrames: 1500).Select(r => r.StartFrame)
                 .SequenceEqual([6000, 3000]) &&                        // longest first
              map.SignificantUnusedRuns(minimumFrames: 2500).Select(r => r.StartFrame)
                 .SequenceEqual([6000]));
        Check("timeline: the filter keeps everything when nothing is trivial",
              map.SignificantUnusedRuns(minimumFrames: 1).Count() == 3);

        Check("timeline: no video means an empty map",
              TimelineMap.Build(new LdpProject(), 0).TotalFrames == 0);
        Check("timeline: an out-of-range video index is empty",
              TimelineMap.Build(project, 7).TotalFrames == 0 &&
              TimelineMap.Build(project, -1).TotalFrames == 0);
    }

    private static void MultiVideoChecks(Action<string, bool> Check)
    {
        // Two videos on one continuous frame line, the way Hypseus stacks discs.
        var project = new LdpProject { Name = "Two videos" };
        project.Videos.Add(new VideoSource { Path = "a.m2v", GlobalBase = 0, PictureCount = 1000, Fps = 25 });
        project.Videos.Add(new VideoSource { Path = "b.m2v", GlobalBase = 1000, PictureCount = 1000, Fps = 25 });

        var level = new GameLevel { Title = "Spread", StartFrame = 1, IntroEndFrame = 1 };
        level.SceneIds.Add(AddScene(project, "In A", 100, 199).Id);
        level.SceneIds.Add(AddScene(project, "In B", 1500, 1599).Id);
        project.Levels.Add(level);

        TimelineMap a = TimelineMap.Build(project, 0);
        TimelineMap b = TimelineMap.Build(project, 1);

        Check("timeline: each video maps only its own frames",
              a is { FirstFrame: 0, LastFrame: 999 } && b is { FirstFrame: 1000, LastFrame: 1999 });
        Check("timeline: a scene appears only in the video holding it",
              a.Spans.Count(s => s.ClipId != null) == 1 &&
              b.Spans.Count(s => s.ClipId != null) == 1 &&
              a.Spans.First(s => s.ClipId != null).Name.Contains("In A"));
        Check("timeline: spare footage is per video",
              a.UnusedFrames == 900 && b.UnusedFrames == 900);
        Check("timeline: the second video's runs use global frame numbers",
              b.UnusedRuns.SequenceEqual([(1000, 1499), (1600, 1999)]));

        // A scene straddling the join (nothing should produce one, but a
        // hand-edited project file can) is clamped rather than drawn off the end.
        AddScene(project, "Straddles", 900, 1100);
        project.Levels[0].SceneIds.Add(project.Clips[^1].Id);
        TimelineMap clamped = TimelineMap.Build(project, 0);
        Check("timeline: a straddling scene is clamped to this video",
              clamped.Spans.Any(s => s is { StartFrame: 900, EndFrame: 999 }) &&
              clamped.LastFrame == 999);
    }
}
