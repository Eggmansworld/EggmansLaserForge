using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ldp.Project;

/// <summary>How a source image is fitted to the target picture size.</summary>
public enum StillFit
{
    /// <summary><c>scale=W:H</c> — fills the frame, distorting if the aspect differs.</summary>
    Stretch,

    /// <summary>Scale down to fit, then centre on black bars. Keeps the artwork's shape.</summary>
    Pad,
}

/// <summary>Settings for turning one still image into a short .m2v passage.</summary>
public sealed class StillOptions
{
    /// <summary>Length of the generated passage. Five seconds is long enough for an
    /// instructions screen to be read in the attract loop.</summary>
    public double Seconds { get; set; } = 5.0;

    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;

    /// <summary>Output frame rate. Must be one of MPEG-2's eight legal rates, and
    /// must match every other video in the game (Singe times everything in frames).</summary>
    public double Fps { get; set; } = 24000.0 / 1001.0;

    public bool Fade { get; set; }
    public double FadeInSeconds { get; set; } = 0.5;
    public double FadeOutSeconds { get; set; } = 0.5;

    public StillFit Fit { get; set; } = StillFit.Stretch;

    /// <summary><c>-q:v</c>. 2 is visually lossless on flat artwork and text.</summary>
    public int Quality { get; set; } = 2;

    /// <summary>First frame (0-based, within the generated file) at which the picture
    /// is fully visible — everything before it is still fading up from black. Exact
    /// only because the input frame rate is pinned to the output rate; without that
    /// FFmpeg computes the fade at the image demuxer's own rate (25 fps) and then
    /// rate-converts, which lands the boundary a frame late.</summary>
    public int FirstFullyVisibleFrame =>
        Fade ? StillImage.FrameCount(FadeInSeconds, Fps) : 0;

    /// <summary>The frame a single-frame slot should point at: the middle of the
    /// passage. A still slot freezes on one frame, so it only has to be a frame
    /// that is definitely at full brightness — the midpoint always is (fades that
    /// could meet in the middle are rejected by <see cref="StillImage.Validate"/>),
    /// and it does not depend on FFmpeg's fade rounding.</summary>
    public int MidFrame => StillImage.FrameCount(Seconds, Fps) / 2;
}

/// <summary>
/// Builds the FFmpeg command that turns a still image (PNG/JPG/…) into a short
/// Hypseus-ready MPEG-2 passage:
/// <code>
///   -loop 1 -i art.png -t 5 -r 24000/1001 -vf "scale=1600:900"
///   -c:v mpeg2video -q:v 2 -f mpeg2video out.m2v
/// </code>
/// Singe has no notion of an image file — every menu still, instructions page and
/// difficulty screen is a <em>frame number</em> on the disc — so artwork has to
/// become video before a script can point at it.
///
/// Pure and side-effect free, so it can be unit-tested and shown verbatim in the UI.
/// </summary>
public static class StillImage
{
    /// <summary>Image formats offered as input (all read natively by FFmpeg).</summary>
    public static readonly string[] InputExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".tga", ".gif"];

    public static bool IsImage(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string e in InputExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>One of MPEG-2's legal frame rates. The standard stores the rate as a
    /// 4-bit code, so these eight are the only values an .m2v can carry — anything
    /// else silently becomes the nearest one and the game's frame timing drifts.</summary>
    public sealed record Mpeg2Rate(string Rational, double Fps, string Display)
    {
        public override string ToString() => Display;
    }

    public static readonly Mpeg2Rate[] Mpeg2Rates =
    [
        new("24000/1001", 24000.0 / 1001.0, "23.976 fps  (24000/1001) — film"),
        new("24", 24, "24 fps — film"),
        new("25", 25, "25 fps — PAL"),
        new("30000/1001", 30000.0 / 1001.0, "29.97 fps  (30000/1001) — NTSC"),
        new("30", 30, "30 fps"),
        new("50", 50, "50 fps — PAL double rate"),
        new("60000/1001", 60000.0 / 1001.0, "59.94 fps  (60000/1001)"),
        new("60", 60, "60 fps"),
    ];

    /// <summary>The legal MPEG-2 rate closest to <paramref name="fps"/>.</summary>
    public static Mpeg2Rate NearestRate(double fps)
    {
        Mpeg2Rate best = Mpeg2Rates[0];
        double bestGap = double.MaxValue;
        foreach (Mpeg2Rate r in Mpeg2Rates)
        {
            double gap = Math.Abs(r.Fps - fps);
            if (gap < bestGap) { bestGap = gap; best = r; }
        }
        return best;
    }

    /// <summary>True when <paramref name="fps"/> IS one of the legal rates (within
    /// the rounding a probe reports, e.g. 23.976 for 24000/1001).</summary>
    public static bool IsLegalRate(double fps) => Math.Abs(NearestRate(fps).Fps - fps) <= 0.01;

    /// <summary>Frames FFmpeg emits for a duration at a rate: it writes a frame at
    /// every output tick strictly inside the duration, so 5 s at 23.976 gives 120.</summary>
    public static int FrameCount(double seconds, double fps)
    {
        if (seconds <= 0 || fps <= 0) return 0;
        return (int)Math.Ceiling(seconds * fps - 1e-6);
    }

    /// <summary>The <c>-vf</c> chain: fit the artwork to the picture size, then
    /// optionally fade up from and down to black.</summary>
    public static string BuildFilter(StillOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(o.Fit == StillFit.Pad
            ? $"scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease," +
              $"pad={o.Width}:{o.Height}:(ow-iw)/2:(oh-ih)/2"
            : $"scale={o.Width}:{o.Height}");

        if (o.Fade)
        {
            if (o.FadeInSeconds > 0)
                sb.Append($",fade=t=in:st=0:d={Num(o.FadeInSeconds)}");
            if (o.FadeOutSeconds > 0)
            {
                // The fade-out has to start early enough to finish on the last
                // frame; a fade running past the end is simply never seen.
                double start = Math.Max(0, o.Seconds - o.FadeOutSeconds);
                sb.Append($",fade=t=out:st={Num(start)}:d={Num(o.FadeOutSeconds)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The full argument list. <c>-y</c> is included so the displayed
    /// command also runs non-interactively.</summary>
    public static IReadOnlyList<string> BuildArgs(string imagePath, string outputPath, StillOptions o)
    {
        string rate = NearestRate(o.Fps).Rational;
        return
        [
            "-y",
            "-loop", "1",
            // The image demuxer runs at 25 fps unless told otherwise, so without
            // this the filters (notably the fades) are computed at 25 and then
            // rate-converted to the output — an unevenly stepped fade whose
            // boundary lands a frame later than asked for. Measured, not assumed.
            "-framerate", rate,
            "-i", imagePath,
            "-t", Num(o.Seconds),
            "-r", rate,
            "-vf", BuildFilter(o),
            "-c:v", "mpeg2video",
            "-q:v", o.Quality.ToString(CultureInfo.InvariantCulture),
            "-f", "mpeg2video",
            outputPath,
        ];
    }

    /// <summary>Default output name for an image: its own base name, so the .m2v
    /// files line up with the artwork they came from. Spaces become underscores —
    /// the frame file lists bare file names and the game folder is kept
    /// space-free by the same rule.</summary>
    public static string SuggestOutputName(string imagePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(imagePath);
        if (string.IsNullOrWhiteSpace(baseName)) return "output.m2v";
        var sb = new StringBuilder(baseName.Length);
        foreach (char c in baseName)
            sb.Append(c == ' ' || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString() + ".m2v";
    }

    /// <summary>Problems that would produce an unusable passage. Empty when the
    /// settings are sound.</summary>
    public static IReadOnlyList<string> Validate(StillOptions o)
    {
        var problems = new List<string>();

        if (o.Seconds <= 0)
            problems.Add("Length must be more than zero seconds.");
        if (o.Width <= 0 || o.Height <= 0)
            problems.Add("Width and height must both be set.");
        else if (o.Width % 2 != 0 || o.Height % 2 != 0)
            problems.Add($"MPEG-2 needs even dimensions — {o.Width}×{o.Height} has an odd side.");

        if (!IsLegalRate(o.Fps))
            problems.Add($"{o.Fps:0.###} fps is not an MPEG-2 frame rate; " +
                         $"the file would be written at {NearestRate(o.Fps).Fps:0.###} fps " +
                         "and its frame timing would not match the rest of the game.");

        if (o.Fade)
        {
            if (o.FadeInSeconds < 0 || o.FadeOutSeconds < 0)
                problems.Add("Fade durations cannot be negative.");
            else if (o.FadeInSeconds + o.FadeOutSeconds > o.Seconds)
                problems.Add($"The fades total {Num(o.FadeInSeconds + o.FadeOutSeconds)}s but the passage is " +
                             $"only {Num(o.Seconds)}s — the picture never reaches full brightness.");
        }

        if (FrameCount(o.Seconds, o.Fps) < 2)
            problems.Add("That length is under two frames — make it longer.");

        return problems;
    }

    /// <summary>Trims a double to a compact invariant literal (0.5 → "0.5", 6.0 → "6").</summary>
    private static string Num(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
