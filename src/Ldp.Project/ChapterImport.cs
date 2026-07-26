using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>
/// Turns a source video's chapter markers into ready-made scenes. Chapter times
/// live on the source's timeline, which the converted .m2v shares, so
/// <c>frame = round(seconds × fps)</c> mapped into the video's global frame range.
/// Scenes are named "Chapter X (imported)" by convention.
/// </summary>
public static class ChapterImport
{
    /// <summary>
    /// Digits a chapter number is padded to. Two as a floor, so a 36-chapter
    /// film gets "Chapter 01" … "Chapter 36" and a 120-chapter one gets
    /// "Chapter 001" … "Chapter 120". Padding keeps the names in order in every
    /// place that shows them plainly - the storyboard, level lists, the
    /// generated readme - not only where the app can apply a natural sort.
    /// </summary>
    public static int NumberWidth(int highestChapter) =>
        Math.Max(2, Math.Max(1, highestChapter).ToString(CultureInfo.InvariantCulture).Length);

    public static string SceneName(int chapterNumber, int highestChapter) =>
        $"Chapter {chapterNumber.ToString(CultureInfo.InvariantCulture).PadLeft(NumberWidth(highestChapter), '0')} (imported)";

    // Only names this class generated are ever rewritten; anything an author
    // typed themselves is left exactly as they typed it.
    private static readonly Regex ImportedName =
        new(@"^Chapter (\d+) \(imported\)$", RegexOptions.Compiled);

    /// <summary>
    /// Re-pads existing "Chapter N (imported)" scenes to a consistent width and
    /// reports how many changed. Projects imported before padding existed have
    /// a mix of one- and two-digit names, which reads as scrambled wherever the
    /// list is shown in plain alphabetical order.
    /// </summary>
    public static int RenumberImported(IEnumerable<Clip> clips)
    {
        var found = new List<(Clip Clip, int Number)>();
        foreach (Clip clip in clips)
            if (ImportedName.Match(clip.Name) is { Success: true } m)
                found.Add((clip, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        if (found.Count == 0) return 0;

        int highest = found.Max(f => f.Number);
        int changed = 0;
        foreach ((Clip clip, int number) in found)
        {
            string padded = SceneName(number, highest);
            if (clip.Name == padded) continue;
            clip.Name = padded;
            changed++;
        }
        return changed;
    }

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
        // Pad to the highest number in the set, so every name from one import
        // is the same width whether or not a chapter turns out degenerate.
        int highest = ordered.Max(c => c.Number);

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
                Name = SceneName(ordered[i].Number, highest),
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
