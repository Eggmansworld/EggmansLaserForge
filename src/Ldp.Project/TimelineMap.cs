using System;
using System.Collections.Generic;
using System.Linq;

namespace Ldp.Project;

/// <summary>What a stretch of video is being used for.</summary>
public enum TimelineRole
{
    /// <summary>A scene a level plays: the game itself.</summary>
    Gameplay,

    /// <summary>A level's intro clip — Level[n]'s first two frames, played before its scenes.</summary>
    LevelIntro,

    /// <summary>A death scene (the pool, plus anything a move or a Death wire names).</summary>
    Death,

    /// <summary>A framework video slot: attract, title, Get Ready, Game Over…</summary>
    Slot,

    /// <summary>A single-frame still slot: a menu screen, the trophy, a difficulty card.</summary>
    Still,

    /// <summary>
    /// A scene that exists in the bin but nothing references — no level plays it,
    /// no move dies to it, no slot holds it. Not the same as unused video: the
    /// author marked this deliberately and then never wired it in, which is
    /// usually a job left half-done rather than spare footage.
    /// </summary>
    Unassigned,
}

/// <summary>One labelled stretch of the timeline. Both ends are inclusive.</summary>
public sealed record TimelineSpan(int StartFrame, int EndFrame, TimelineRole Role, string Name, Guid? ClipId)
{
    public int FrameCount => EndFrame - StartFrame + 1;
}

/// <summary>A level's extent in this video, for the band above the coverage row.</summary>
public sealed record TimelineLevelBand(int Number, string Title, int StartFrame, int EndFrame)
{
    public int FrameCount => EndFrame - StartFrame + 1;
}

/// <summary>
/// Everything one video of a project is and isn't used for, as spans on its own
/// frame line.
///
/// The strip above the transport slider used to draw the selected scene and
/// nothing else, so a 100,000-frame video showed one short band floating in
/// empty space — with no way to tell the empty space that is *spare footage*
/// from the empty space that is simply the rest of the game. This builds the
/// whole picture instead: every scene with the job it does, and the runs of
/// video no part of the game touches.
///
/// <see cref="UnusedRuns"/> is the same answer a "blank the leftover video"
/// tool needs, which is why the arithmetic lives here in the project layer
/// rather than inside a control's paint method.
/// </summary>
public sealed class TimelineMap
{
    /// <summary>Every used stretch, in frame order. One span per scene.</summary>
    public IReadOnlyList<TimelineSpan> Spans { get; private init; } = [];

    /// <summary>Level extents within this video, in play order.</summary>
    public IReadOnlyList<TimelineLevelBand> Levels { get; private init; } = [];

    /// <summary>
    /// The runs no span covers, in frame order, both ends inclusive. This is the
    /// leftover footage — what a finished project could blank to shrink the file.
    /// </summary>
    public IReadOnlyList<(int StartFrame, int EndFrame)> UnusedRuns { get; private init; } = [];

    /// <summary>
    /// First and last frame of the video this maps, inclusive. A map with no
    /// video leaves Last one BELOW First, which is what makes
    /// <see cref="TotalFrames"/> come out at zero rather than at one — the
    /// difference between "no video loaded" and "a video with a single frame".
    /// </summary>
    public int FirstFrame { get; private init; }
    public int LastFrame { get; private init; } = -1;

    public int TotalFrames => LastFrame - FirstFrame + 1;

    /// <summary>
    /// Frames at least one span covers. A union, not a sum: scenes may overlap
    /// (a death reused by two levels, a slot sitting inside a level's footage),
    /// and adding their lengths would report more used frames than the video has.
    /// </summary>
    public int UsedFrames { get; private init; }

    public int UnusedFrames => TotalFrames - UsedFrames;

    /// <summary>Share of the video the game touches, 0..1.</summary>
    public double UsedFraction => TotalFrames > 0 ? UsedFrames / (double)TotalFrames : 0;

    /// <summary>An empty map, for when there is no project or no video loaded.</summary>
    public static readonly TimelineMap Empty = new();

    /// <summary>
    /// Builds the map for one video. Spans are clamped to that video, so a scene
    /// straddling two videos (which nothing should produce, but a hand-edited
    /// project file can) contributes only the part that belongs here.
    /// </summary>
    public static TimelineMap Build(LdpProject project, int videoIndex)
    {
        if (project == null || videoIndex < 0 || videoIndex >= project.Videos.Count) return Empty;
        VideoSource video = project.Videos[videoIndex];
        if (video.PictureCount <= 0) return Empty;

        int first = video.GlobalBase;
        int last = video.GlobalBase + video.PictureCount - 1;

        // ---- What each scene is for ----
        //
        // A scene can qualify for more than one role: an author may wire the
        // same footage as a level's scene and as a death. One block per scene
        // reads far better than stacked translucent bands, so the most specific
        // job wins — a death is a death even if a level also plays it.
        var deaths = project.DeathScenes().ToHashSet();
        var slotNames = new Dictionary<Guid, string>();
        foreach ((RangeSlot slot, Guid id) in project.Slots.Ranges)
            if (!slotNames.ContainsKey(id))
                slotNames[id] = SlotCatalog.Ranges.FirstOrDefault(r => r.Slot == slot)?.Display ?? slot.ToString();

        Dictionary<Guid, (int Level, int Scene)> placement = project.LevelPositions();

        var spans = new List<TimelineSpan>();
        foreach (Clip clip in project.Clips)
        {
            if (clip.EndFrame < first || clip.StartFrame > last) continue;

            (TimelineRole role, string name) = deaths.Contains(clip.Id)
                ? (TimelineRole.Death, clip.Name)
                : slotNames.TryGetValue(clip.Id, out string? slotName)
                    ? (TimelineRole.Slot, slotName)
                    : placement.TryGetValue(clip.Id, out (int Level, int Scene) at)
                        ? (TimelineRole.Gameplay, $"L{at.Level} S{at.Scene} · {clip.Name}")
                        : (TimelineRole.Unassigned, clip.Name);

            spans.Add(new TimelineSpan(Math.Max(first, clip.StartFrame), Math.Min(last, clip.EndFrame),
                                       role, name, clip.Id));
        }

        // ---- Level intro clips ----
        // Level[n] = {title, start, introEnd, …} plays start..introEnd before the
        // level's first scene. That is footage the game shows, so it counts as
        // used — but only when the level actually declares one; the framework's
        // "no intro" encoding is a gap under two frames.
        foreach (GameLevel level in project.Levels)
        {
            if (!LevelIntro.DeclaresIntro(level.StartFrame, level.IntroEndFrame)) continue;
            if (level.IntroEndFrame < first || level.StartFrame > last) continue;
            spans.Add(new TimelineSpan(Math.Max(first, level.StartFrame), Math.Min(last, level.IntroEndFrame),
                                       TimelineRole.LevelIntro, $"Intro · {level.Title}", null));
        }

        // ---- Still slots ----
        foreach ((StillSlot slot, int frame) in project.Slots.Stills)
        {
            if (frame < first || frame > last) continue;
            string name = SlotCatalog.Stills.FirstOrDefault(s => s.Slot == slot)?.Display ?? slot.ToString();
            spans.Add(new TimelineSpan(frame, frame, TimelineRole.Still, name, null));
        }

        spans.Sort((a, b) => a.StartFrame != b.StartFrame
            ? a.StartFrame.CompareTo(b.StartFrame)
            : a.EndFrame.CompareTo(b.EndFrame));

        // ---- Level bands ----
        var bands = new List<TimelineLevelBand>();
        for (int i = 0; i < project.Levels.Count; i++)
        {
            GameLevel level = project.Levels[i];
            var frames = level.SceneIds
                .Select(project.ClipById)
                .Where(c => c != null && c!.EndFrame >= first && c.StartFrame <= last)
                .ToList();
            int start, end;
            if (frames.Count > 0)
            {
                start = frames.Min(c => c!.StartFrame);
                end = frames.Max(c => c!.EndFrame);
                // The intro belongs to the level too, and runs ahead of scene one.
                if (LevelIntro.DeclaresIntro(level.StartFrame, level.IntroEndFrame) &&
                    level.StartFrame >= first && level.StartFrame < start)
                    start = level.StartFrame;
            }
            else if (LevelIntro.DeclaresIntro(level.StartFrame, level.IntroEndFrame) &&
                     level.IntroEndFrame >= first && level.StartFrame <= last)
            {
                start = level.StartFrame;
                end = level.IntroEndFrame;
            }
            else
            {
                continue; // this level has nothing in this video
            }
            bands.Add(new TimelineLevelBand(i + 1, level.Title,
                                            Math.Max(first, start), Math.Min(last, end)));
        }

        // ---- Used / unused ----
        // Merge overlapping spans into runs, then take the complement. Doing it
        // by merge rather than a per-frame flag keeps this cheap on a 200,000
        // frame video, which is an ordinary size for a feature film.
        var merged = new List<(int Start, int End)>();
        foreach (TimelineSpan span in spans)
        {
            if (merged.Count > 0 && span.StartFrame <= merged[^1].End + 1)
            {
                if (span.EndFrame > merged[^1].End) merged[^1] = (merged[^1].Start, span.EndFrame);
            }
            else
            {
                merged.Add((span.StartFrame, span.EndFrame));
            }
        }

        int used = merged.Sum(r => r.End - r.Start + 1);

        var unused = new List<(int, int)>();
        int cursor = first;
        foreach ((int start, int end) in merged)
        {
            if (start > cursor) unused.Add((cursor, start - 1));
            cursor = Math.Max(cursor, end + 1);
        }
        if (cursor <= last) unused.Add((cursor, last));

        return new TimelineMap
        {
            Spans = spans,
            Levels = bands,
            UnusedRuns = unused,
            FirstFrame = first,
            LastFrame = last,
            UsedFrames = used,
        };
    }

    /// <summary>
    /// The span under a frame, or null when that frame is spare footage. The
    /// most specific one wins where scenes overlap — the shortest span covering
    /// the frame is the one that describes it best.
    /// </summary>
    public TimelineSpan? SpanAt(int frame)
    {
        TimelineSpan? best = null;
        foreach (TimelineSpan span in Spans)
        {
            if (span.StartFrame > frame) break; // sorted by start
            if (span.EndFrame < frame) continue;
            if (best == null || span.FrameCount < best.FrameCount) best = span;
        }
        return best;
    }

    /// <summary>The level covering a frame, or null.</summary>
    public TimelineLevelBand? LevelAt(int frame) =>
        Levels.FirstOrDefault(b => frame >= b.StartFrame && frame <= b.EndFrame);

    /// <summary>
    /// Unused runs worth acting on, longest first. Short gaps between scenes are
    /// rounding, not spare footage — blanking a handful of frames saves nothing
    /// and only risks clipping the edge of a scene.
    /// </summary>
    public IEnumerable<(int StartFrame, int EndFrame)> SignificantUnusedRuns(int minimumFrames = 60) =>
        UnusedRuns.Where(r => r.EndFrame - r.StartFrame + 1 >= minimumFrames)
                  .OrderByDescending(r => r.EndFrame - r.StartFrame);
}
