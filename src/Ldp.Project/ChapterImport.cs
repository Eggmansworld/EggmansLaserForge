using System;
using System.Collections.Generic;
using System.Linq;

namespace Ldp.Project;

/// <summary>
/// Turns a source video's chapter markers into ready-made scenes. Chapter times
/// live on the source's timeline, which the converted .m2v shares, so
/// <c>frame = round(seconds × fps)</c> mapped into the video's global frame range.
/// Scenes are named "Chapter X (imported)" by convention.
/// </summary>
public static class ChapterImport
{
    public static string SceneName(int chapterNumber) => $"Chapter {chapterNumber} (imported)";

    /// <summary>
    /// Builds one scene per chapter for a project video. Each scene runs from its
    /// chapter's start frame to the frame before the next chapter (the last one
    /// uses its own end time), clamped to the video. Degenerate chapters (empty
    /// after rounding) are skipped; numbering still follows the chapter order.
    /// </summary>
    public static List<Clip> BuildScenes(IReadOnlyList<ChapterInfo> chapters, double fps,
                                         int globalBase, int pictureCount)
    {
        var result = new List<Clip>();
        if (chapters.Count == 0 || fps <= 0 || pictureCount <= 0) return result;

        List<ChapterInfo> ordered = chapters.OrderBy(c => c.StartSeconds).ToList();
        int lastFrame = pictureCount - 1;

        for (int i = 0; i < ordered.Count; i++)
        {
            int start = Math.Clamp((int)Math.Round(ordered[i].StartSeconds * fps), 0, lastFrame);
            int end = i + 1 < ordered.Count
                ? (int)Math.Round(ordered[i + 1].StartSeconds * fps) - 1
                : (int)Math.Round(ordered[i].EndSeconds * fps) - 1;
            end = Math.Clamp(end, 0, lastFrame);
            if (end <= start) continue; // degenerate (sub-frame) chapter

            result.Add(new Clip
            {
                Name = SceneName(ordered[i].Number),
                Description = $"Auto-generated from chapter {ordered[i].Number} " +
                              $"({FormatTime(ordered[i].StartSeconds)} in the source video).",
                StartFrame = globalBase + start,
                EndFrame = globalBase + end,
            });
        }
        return result;
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
