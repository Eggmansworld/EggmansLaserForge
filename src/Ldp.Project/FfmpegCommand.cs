using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ldp.Project;

/// <summary>MPEG-2 encode quality for the .m2v output. Mirrors the two presets in
/// the author's proven FFmpeg script, plus a free-bitrate option.</summary>
public enum VideoQuality
{
    /// <summary><c>-qscale:v 1</c> — highest-quality working master, no bitrate cap (large files).</summary>
    Highest,

    /// <summary><c>-qscale:v 4 -b:v 6000k</c> — respectable release size, excellent quality.</summary>
    Balanced,

    /// <summary><c>-qscale:v 4 -b:v {custom}k</c> — user-chosen bitrate.</summary>
    Custom,
}

/// <summary>Vorbis encode quality for the .ogg output.</summary>
public enum AudioQuality
{
    /// <summary>44.1 kHz, 160 kbps — the script default.</summary>
    Standard,

    /// <summary>44.1 kHz, user-chosen bitrate (up to 320 kbps).</summary>
    Custom,
}

/// <summary>User-chosen conversion settings, independent of any single input file.
/// Per-file choices (audio track, downscale) are passed to <see cref="FfmpegCommand.Build"/>.</summary>
public sealed class ConvertOptions
{
    public VideoQuality Video { get; set; } = VideoQuality.Highest;
    public int CustomVideoBitrateK { get; set; } = 6000;

    /// <summary>Skip the video pass entirely and only produce .ogg audio — for
    /// coming back to an already-converted .m2v to add language tracks (or redo
    /// the audio) without re-encoding the picture.</summary>
    public bool AudioOnly { get; set; }

    public bool CreateAudio { get; set; } = true;
    public AudioQuality Audio { get; set; } = AudioQuality.Standard;
    public int CustomAudioBitrateK { get; set; } = 160;

    /// <summary>Downmix the chosen track to stereo (<c>-ac 2</c>). Default on:
    /// Singe games play stereo, and libvorbis refuses low bitrates on 5.1/7.1
    /// sources (the "encoder setup failed" error on multi-channel movie tracks).</summary>
    public bool DownmixStereo { get; set; } = true;

    /// <summary>Audio pre-seek in milliseconds. The author's script uses 110 ms to
    /// nudge A/V sync; 0 disables the <c>-ss</c> offset entirely.</summary>
    public int AudioOffsetMs { get; set; } = 110;

    public int EffectiveVideoBitrateK => Video == VideoQuality.Custom ? CustomVideoBitrateK : 6000;
    public int EffectiveAudioBitrateK => Audio == AudioQuality.Custom ? CustomAudioBitrateK : 160;
}

/// <summary>One extra "language track" export: another audio stream of the same
/// source, written as <c>{video name}{Suffix}.ogg</c> (e.g. <c>Alita-fre.ogg</c>)
/// for Hypseus's LangOpt language switching.</summary>
public sealed record FfmpegLangJob(
    IReadOnlyList<string> Args,
    string OggPath,
    string Suffix,
    string LanguageName);

/// <summary>One file's conversion: the two FFmpeg invocations (video, then audio),
/// exactly as the author's batch script runs them, plus any language-track exports.</summary>
public sealed record FfmpegJob(
    string InputPath,
    IReadOnlyList<string> VideoArgs,
    string M2vPath,
    IReadOnlyList<string>? AudioArgs,
    string? OggPath,
    IReadOnlyList<FfmpegLangJob> LanguageAudio);

/// <summary>
/// Builds the FFmpeg command lines that turn a source video (mkv/mp4/webm, …) into a
/// Hypseus-ready MPEG-2 elementary stream (.m2v) and a matching Vorbis .ogg.
/// The flags are a faithful port of the author's tested script:
/// <code>
///   video: [-vf scale=W:H] -an -qscale:v N [-b:v Kk] -codec:v mpeg2video  out.m2v
///   audio: [-ss OFFSET] -vn -c:a libvorbis -ar 44100 -map a -b:a Kk        out.ogg
/// </code>
/// Pure and side-effect free, so it can be unit-tested and shown verbatim in the UI.
/// <c>-y</c> is added so the displayed command also runs non-interactively (FFmpeg
/// would otherwise stop to ask before overwriting).
/// </summary>
public static class FfmpegCommand
{
    /// <summary>Source containers offered for conversion (mkv/mp4/webm are the common ones;
    /// the rest are accepted because FFmpeg handles them just as well).</summary>
    public static readonly string[] InputExtensions =
        [".mkv", ".mp4", ".webm", ".mov", ".m4v", ".avi", ".ts"];

    public static bool IsConvertibleInput(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string e in InputExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Builds the job for one input file. Outputs land next to the source
    /// (or in <paramref name="outputDir"/>) under the source's base name.
    /// <paramref name="audioTrack"/> selects which audio stream becomes the main
    /// .ogg (<c>-map 0:a:{n}</c> — mapping ALL tracks, as <c>-map a</c> does, breaks
    /// on multi-language sources). <paramref name="scale"/> optionally downscales
    /// the picture (aspect handled by the caller; Hypseus tops out at 1080p).
    /// <paramref name="languageTracks"/> lists extra audio streams to export as
    /// <c>{base}{suffix}.ogg</c> language tracks with the same audio settings.</summary>
    public static FfmpegJob Build(string inputPath, ConvertOptions o, string? outputDir = null,
                                  int audioTrack = 0, (int Width, int Height)? scale = null,
                                  IReadOnlyList<(int Track, string Suffix, string LanguageName)>? languageTracks = null)
    {
        string dir = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string m2v = Path.Combine(dir, baseName + ".m2v");
        string ogg = Path.Combine(dir, baseName + ".ogg");

        // Audio-only mode leaves the video args empty: the .m2v already exists
        // and its path is kept only so the caller can pair outputs to it.
        var video = new List<string>();
        if (!o.AudioOnly)
        {
            video.AddRange(["-y", "-i", inputPath]);
            if (scale is { } s)
                video.AddRange(["-vf", $"scale={s.Width}:{s.Height}"]);
            video.Add("-an");
            video.AddRange(o.Video switch
            {
                VideoQuality.Highest => (IEnumerable<string>)["-qscale:v", "1"],
                VideoQuality.Balanced => ["-qscale:v", "4", "-b:v", "6000k"],
                VideoQuality.Custom => ["-qscale:v", "4", "-b:v", $"{o.CustomVideoBitrateK}k"],
                _ => ["-qscale:v", "1"],
            });
            video.AddRange(["-codec:v", "mpeg2video", m2v]);
        }

        List<string> AudioArgsFor(int track, string outPath)
        {
            List<string> args = ["-y", "-i", inputPath];
            if (o.AudioOffsetMs > 0)
                args.AddRange(["-ss", FormatOffset(o.AudioOffsetMs)]);
            args.AddRange(["-vn", "-c:a", "libvorbis", "-ar", "44100",
                           "-map", $"0:a:{Math.Max(0, track)}"]);
            if (o.DownmixStereo)
                args.AddRange(["-ac", "2"]);
            args.AddRange(["-b:a", $"{o.EffectiveAudioBitrateK}k", outPath]);
            return args;
        }

        List<string>? audio = o.CreateAudio ? AudioArgsFor(audioTrack, ogg) : null;

        List<FfmpegLangJob> langs = [];
        if (o.CreateAudio && languageTracks != null)
            foreach ((int track, string suffix, string name) in languageTracks)
            {
                string langOgg = Path.Combine(dir, baseName + suffix + ".ogg");
                langs.Add(new FfmpegLangJob(AudioArgsFor(track, langOgg), langOgg, suffix, name));
            }

        return new FfmpegJob(inputPath, video, m2v, audio, o.CreateAudio ? ogg : null, langs);
    }

    /// <summary>Formats a millisecond offset as FFmpeg's <c>HH:MM:SS.mmm</c> timestamp
    /// (110 → <c>00:00:00.110</c>).</summary>
    public static string FormatOffset(int ms) =>
        TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    /// <summary>Renders an argument list as a copy-pasteable command line, quoting
    /// tokens that contain spaces (paths, mainly).</summary>
    public static string Display(string ffmpegExe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(Quote(ffmpegExe));
        foreach (string a in args)
        {
            sb.Append(' ');
            sb.Append(Quote(a));
        }
        return sb.ToString();
    }

    private static string Quote(string token) =>
        token.Length == 0 || token.Contains(' ') ? $"\"{token}\"" : token;
}
