using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>One audio stream of a source video, as reported by FFmpeg.
/// <paramref name="Ordinal"/> is the position among AUDIO streams only —
/// exactly what <c>-map 0:a:{Ordinal}</c> selects.</summary>
public sealed record AudioTrackInfo(
    int Ordinal,
    string Codec,
    string Language,
    string Channels,
    int SampleRate,
    bool IsDefault,
    string Title)
{
    public bool IsMultiChannel =>
        !Channels.StartsWith("mono", StringComparison.OrdinalIgnoreCase) &&
        !Channels.StartsWith("stereo", StringComparison.OrdinalIgnoreCase) &&
        !Channels.StartsWith("1 ch", StringComparison.OrdinalIgnoreCase) &&
        !Channels.StartsWith("2 ch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Combo-box text, e.g.
    /// <c>[eng] TrueHD 7.1 Atmos — truehd (Dolby TrueHD + Dolby Atmos) · 7.1 · 48000 Hz (default)</c>.</summary>
    public string Display()
    {
        string lang = string.IsNullOrEmpty(Language) ? "und" : Language;
        string name = string.IsNullOrEmpty(Title) ? Codec : $"{Title} — {Codec}";
        string rate = SampleRate > 0 ? $" · {SampleRate} Hz" : "";
        return $"[{lang}] {name} · {Channels}{rate}{(IsDefault ? " (default)" : "")}";
    }
}

/// <summary>One chapter marker of a source video: its 1-based number and its time
/// span in seconds on the source timeline (which the converted .m2v shares).</summary>
public sealed record ChapterInfo(int Number, double StartSeconds, double EndSeconds);

/// <summary>
/// What LaserForge needs to know about a source video before converting it:
/// resolution/fps, every audio track with pickable metadata, and any chapter
/// markers. Built by parsing FFmpeg's own <c>-i</c> stream dump (stderr) so it
/// works with the exact FFmpeg the user pointed at — no extra probing tool required.
/// </summary>
public sealed class MediaInfo
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Fps { get; private set; }
    public string VideoCodec { get; private set; } = "";
    public TimeSpan? Duration { get; private set; }
    public List<AudioTrackInfo> AudioTracks { get; } = [];
    public List<ChapterInfo> Chapters { get; } = [];

    public bool HasVideo => Width > 0 && Height > 0;

    /// <summary>True when the picture is larger than Hypseus's practical 1080p
    /// ceiling (playback gets too CPU-heavy above that).</summary>
    public bool ExceedsHypseusLimit => Height > 1080 || Width > 1920;

    /// <summary>The audio track a player would pick by default: the default-flagged
    /// one, else the first. -1 when the file has no audio.</summary>
    public int DefaultAudioTrack
    {
        get
        {
            if (AudioTracks.Count == 0) return -1;
            foreach (AudioTrackInfo t in AudioTracks)
                if (t.IsDefault) return t.Ordinal;
            return 0;
        }
    }

    // Stream #0:1(eng): Audio: ...   |   Stream #0:1[0x2](und): Audio: ...
    private static readonly Regex StreamRx = new(
        @"^\s*Stream\s+#\d+:\d+(?:\[[^\]]*\])?(?:\((?<lang>[^)]*)\))?:\s*(?<kind>Video|Audio):\s*(?<detail>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"^\s*title\s*:\s*(?<t>.+)$", RegexOptions.Compiled);
    private static readonly Regex DurationRx = new(
        @"^\s*Duration:\s*(?<h>\d+):(?<m>\d\d):(?<s>\d\d(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(@"(?<w>\d{2,5})x(?<h>\d{2,5})", RegexOptions.Compiled);
    private static readonly Regex FpsRx = new(@"(?<fps>\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);
    private static readonly Regex HzRx = new(@"(?<hz>\d+)\s*Hz", RegexOptions.Compiled);
    private static readonly Regex ChapterRx = new(
        @"^\s*Chapter\s+#\d+:\d+:\s*start\s+(?<s>\d+(?:\.\d+)?),\s*end\s+(?<e>\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    /// <summary>Parses the stderr text of <c>ffmpeg -i file</c> (the run "fails"
    /// with "At least one output file must be specified" — that's expected).</summary>
    public static MediaInfo Parse(string ffmpegStderr)
    {
        var info = new MediaInfo();
        bool lastWasAudio = false;

        foreach (string rawLine in ffmpegStderr.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (info.Duration == null && DurationRx.Match(line) is { Success: true } d)
            {
                double sec = double.Parse(d.Groups["s"].Value, CultureInfo.InvariantCulture);
                info.Duration = new TimeSpan(0, int.Parse(d.Groups["h"].Value), int.Parse(d.Groups["m"].Value), 0)
                                + TimeSpan.FromSeconds(sec);
            }

            if (ChapterRx.Match(line) is { Success: true } ch)
            {
                lastWasAudio = false;
                info.Chapters.Add(new ChapterInfo(
                    info.Chapters.Count + 1,
                    double.Parse(ch.Groups["s"].Value, CultureInfo.InvariantCulture),
                    double.Parse(ch.Groups["e"].Value, CultureInfo.InvariantCulture)));
                continue;
            }

            Match m = StreamRx.Match(line);
            if (!m.Success)
            {
                // A "title" metadata line right after an audio stream names it
                // the way players (and users) know it, e.g. "TrueHD 7.1 Atmos".
                if (lastWasAudio && info.AudioTracks.Count > 0 &&
                    TitleRx.Match(line) is { Success: true } t)
                {
                    AudioTrackInfo last = info.AudioTracks[^1];
                    if (last.Title.Length == 0)
                        info.AudioTracks[^1] = last with { Title = t.Groups["t"].Value.Trim() };
                    lastWasAudio = false; // only the first title line applies
                }
                continue;
            }

            string detail = m.Groups["detail"].Value;
            if (m.Groups["kind"].Value == "Video")
            {
                lastWasAudio = false;
                if (info.HasVideo) continue; // first video stream wins (covers/pgs come later)
                string[] vparts = detail.Split(',');
                info.VideoCodec = vparts[0].Trim();
                // Resolution appears in a part of its own ("3840x2160 [SAR ...]"),
                // never inside the codec name; scan parts after the codec.
                for (int i = 1; i < vparts.Length && info.Width == 0; i++)
                    if (SizeRx.Match(vparts[i]) is { Success: true } s)
                    {
                        info.Width = int.Parse(s.Groups["w"].Value);
                        info.Height = int.Parse(s.Groups["h"].Value);
                    }
                if (FpsRx.Match(detail) is { Success: true } f)
                    info.Fps = double.Parse(f.Groups["fps"].Value, CultureInfo.InvariantCulture);
            }
            else // Audio
            {
                lastWasAudio = true;
                string[] parts = detail.Split(", ");
                string codec = parts[0].Trim();
                int rate = 0;
                string channels = "";
                for (int i = 1; i < parts.Length; i++)
                {
                    if (rate == 0 && HzRx.Match(parts[i]) is { Success: true } hz)
                    {
                        rate = int.Parse(hz.Groups["hz"].Value);
                        // FFmpeg's order is fixed: codec, rate, channel layout, ...
                        if (i + 1 < parts.Length)
                            channels = parts[i + 1].Trim();
                        break;
                    }
                }
                info.AudioTracks.Add(new AudioTrackInfo(
                    Ordinal: info.AudioTracks.Count,
                    Codec: codec,
                    Language: m.Groups["lang"].Value,
                    Channels: channels,
                    SampleRate: rate,
                    IsDefault: detail.Contains("(default)"),
                    Title: ""));
            }
        }
        return info;
    }

    /// <summary>
    /// Aspect-ratio-preserving downscale choices for a source, largest first:
    /// heights 1080/900/720/576/480 that are strictly smaller than the source,
    /// widths rounded to even (MPEG-2 needs even dimensions). A 16:9 4K master
    /// yields 1920×1080 … 854×480; a 4:3 source bottoms out at 640×480.
    /// </summary>
    public static IReadOnlyList<(int Width, int Height)> DownscalePresets(int srcWidth, int srcHeight)
    {
        var result = new List<(int, int)>();
        if (srcWidth <= 0 || srcHeight <= 0) return result;
        foreach (int h in (int[])[1080, 900, 720, 576, 480])
        {
            if (h >= srcHeight) continue;
            int w = (int)Math.Round(srcWidth * (double)h / srcHeight / 2, MidpointRounding.AwayFromZero) * 2;
            result.Add((w, h));
        }
        return result;
    }
}
