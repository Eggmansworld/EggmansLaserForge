using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Exact-argument checks for the FFmpeg command builder, plus the stream-dump
/// parser behind the audio-track picker. These lock the generated command lines
/// to the author's proven script (mpeg2video video with the qscale presets;
/// libvorbis audio with the 44.1 kHz / single-track map / stereo downmix /
/// -ss offset shape) so a refactor can't silently change what gets run.
/// </summary>
public static class FfmpegCommandTest
{
    public static void Run(Action<string, bool> Check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ldp-ffmpeg-test");
        string input = Path.Combine(dir, "clip.mkv");
        string m2v = Path.Combine(dir, "clip.m2v");
        string ogg = Path.Combine(dir, "clip.ogg");

        // Highest quality, keep source resolution, standard audio at 110 ms.
        // Audio maps exactly ONE track (-map 0:a:0) and downmixes to stereo:
        // "-map a" mapped every track of multi-language sources and libvorbis
        // refused 160k on 5.1/7.1 ("encoder setup failed" — the Alita log).
        FfmpegJob hi = FfmpegCommand.Build(input, new ConvertOptions(), dir);
        Check("ffmpeg: highest video args exact", hi.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-an", "-qscale:v", "1", "-codec:v", "mpeg2video", m2v]));
        Check("ffmpeg: m2v path beside source", hi.M2vPath == m2v);
        Check("ffmpeg: standard audio args exact", hi.AudioArgs!.SequenceEqual(
            ["-y", "-i", input, "-ss", "00:00:00.110", "-vn", "-c:a", "libvorbis",
             "-ar", "44100", "-map", "0:a:0", "-ac", "2", "-b:a", "160k", ogg]));
        Check("ffmpeg: ogg path beside source", hi.OggPath == ogg);

        // Balanced preset: qscale 4 with the 6000k cap, no scale filter.
        FfmpegJob bal = FfmpegCommand.Build(input, new ConvertOptions { Video = VideoQuality.Balanced }, dir);
        Check("ffmpeg: balanced qscale+bitrate", bal.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-an", "-qscale:v", "4", "-b:v", "6000k", "-codec:v", "mpeg2video", m2v]));

        // Custom video bitrate keeps qscale 4 and swaps -b:v.
        FfmpegJob cust = FfmpegCommand.Build(input,
            new ConvertOptions { Video = VideoQuality.Custom, CustomVideoBitrateK = 8000 }, dir);
        Check("ffmpeg: custom video bitrate", cust.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-an", "-qscale:v", "4", "-b:v", "8000k", "-codec:v", "mpeg2video", m2v]));

        // A downscale inserts -vf scale immediately after the input, before -an.
        FfmpegJob rez = FfmpegCommand.Build(input, new ConvertOptions(), dir, scale: (1920, 1080));
        Check("ffmpeg: downscale adds -vf scale", rez.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-vf", "scale=1920:1080", "-an", "-qscale:v", "1", "-codec:v", "mpeg2video", m2v]));

        // Picking a specific audio track maps that stream only.
        FfmpegJob track3 = FfmpegCommand.Build(input, new ConvertOptions(), dir, audioTrack: 3);
        Check("ffmpeg: audio track selects -map 0:a:3",
            track3.AudioArgs!.SequenceEqual(
                ["-y", "-i", input, "-ss", "00:00:00.110", "-vn", "-c:a", "libvorbis",
                 "-ar", "44100", "-map", "0:a:3", "-ac", "2", "-b:a", "160k", ogg]));

        // Downmix can be turned off for advanced users (keeps source channels).
        FfmpegJob keepCh = FfmpegCommand.Build(input, new ConvertOptions { DownmixStereo = false }, dir);
        Check("ffmpeg: downmix off omits -ac", !keepCh.AudioArgs!.Contains("-ac"));

        // Custom audio bitrate + a different sync offset.
        FfmpegJob a320 = FfmpegCommand.Build(input,
            new ConvertOptions { Audio = AudioQuality.Custom, CustomAudioBitrateK = 320, AudioOffsetMs = 250 }, dir);
        Check("ffmpeg: custom audio bitrate + offset", a320.AudioArgs!.SequenceEqual(
            ["-y", "-i", input, "-ss", "00:00:00.250", "-vn", "-c:a", "libvorbis",
             "-ar", "44100", "-map", "0:a:0", "-ac", "2", "-b:a", "320k", ogg]));

        // A zero offset drops the -ss entirely.
        FfmpegJob noff = FfmpegCommand.Build(input, new ConvertOptions { AudioOffsetMs = 0 }, dir);
        Check("ffmpeg: zero offset omits -ss", !noff.AudioArgs!.Contains("-ss"));

        // Audio can be turned off completely.
        FfmpegJob silent = FfmpegCommand.Build(input, new ConvertOptions { CreateAudio = false }, dir);
        Check("ffmpeg: no-audio job has null audio", silent.AudioArgs == null && silent.OggPath == null);

        // Default output directory is the source's own folder.
        FfmpegJob beside = FfmpegCommand.Build(input, new ConvertOptions());
        Check("ffmpeg: default output beside source",
            Path.GetDirectoryName(beside.M2vPath) == Path.GetDirectoryName(Path.GetFullPath(input)));

        // Offset timestamp formatting.
        Check("ffmpeg: offset 110 -> 00:00:00.110", FfmpegCommand.FormatOffset(110) == "00:00:00.110");
        Check("ffmpeg: offset 1500 -> 00:00:01.500", FfmpegCommand.FormatOffset(1500) == "00:00:01.500");

        // Display quotes paths with spaces and leaves flags bare.
        string cmd = FfmpegCommand.Display(@"C:\Program Files\ff\ffmpeg.exe",
            ["-i", @"C:\my videos\a.mkv", "-an"]);
        Check("ffmpeg: display quotes spaced paths",
            cmd == "\"C:\\Program Files\\ff\\ffmpeg.exe\" -i \"C:\\my videos\\a.mkv\" -an");

        // Convertible-input detection (case-insensitive; .m2v is already playable).
        Check("ffmpeg: mkv/mp4/webm are convertible",
            FfmpegCommand.IsConvertibleInput("a.mkv") && FfmpegCommand.IsConvertibleInput("A.MP4") &&
            FfmpegCommand.IsConvertibleInput("b.webm"));
        Check("ffmpeg: m2v/txt are not convertible",
            !FfmpegCommand.IsConvertibleInput("a.m2v") && !FfmpegCommand.IsConvertibleInput("a.txt"));

        RunMediaInfo(Check);
    }

    /// <summary>MediaInfo parsing against a faithful excerpt of the real Alita 4K
    /// remux dump that broke the original audio conversion (16 audio tracks).</summary>
    private static void RunMediaInfo(Action<string, bool> Check)
    {
        const string alita = """
            Input #0, matroska,webm, from 'Alita.mkv':
              Metadata:
                title           : Alita
              Duration: 02:01:57.38, start: 0.000000, bitrate: 62567 kb/s
              Stream #0:0(eng): Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x2160 [SAR 1:1 DAR 16:9], 23.98 fps, 23.98 tbr, 1k tbn
                Metadata:
                  BPS             : 43498761
              Stream #0:1(eng): Audio: truehd (Dolby TrueHD + Dolby Atmos), 48000 Hz, 7.1, s32 (24 bit) (default)
                Metadata:
                  title           : TrueHD 7.1 Atmos
                  BPS             : 5044847
              Stream #0:2(eng): Audio: dts (dca) (DTS-HD MA), 48000 Hz, 7.1, s32p (24 bit)
                Metadata:
                  title           : DTS-HD MA 7.1
              Stream #0:3(eng): Audio: ac3, 48000 Hz, 5.1(side), fltp, 640 kb/s
                Metadata:
                  title           : DD 5.1
              Stream #0:4(eng): Audio: dts (dca) (DTS-HD MA), 48000 Hz, stereo, s32p (24 bit)
                Metadata:
                  title           : DTS-HD MA 2.0
              Stream #0:5(spa): Audio: ac3, 48000 Hz, 5.1(side), fltp, 448 kb/s
                Metadata:
                  title           : DD 5.1
              Stream #0:6(eng): Subtitle: subrip (srt) (default)
              Stream #0:7(eng): Subtitle: hdmv_pgs_subtitle (pgssub), 1920x1080, start 0.918000
            """;

        MediaInfo info = MediaInfo.Parse(alita);
        Check("probe: video 3840x2160 (subtitle 1920x1080 ignored)",
              info.Width == 3840 && info.Height == 2160);
        Check("probe: fps 23.98", Math.Abs(info.Fps - 23.98) < 0.001);
        Check("probe: video codec", info.VideoCodec == "hevc (Main 10)");
        Check("probe: duration ~2h02m", info.Duration is { } d && Math.Abs((d - new TimeSpan(0, 2, 1, 57, 380)).TotalSeconds) < 1);
        Check("probe: 5 audio tracks, subtitles ignored", info.AudioTracks.Count == 5);

        AudioTrackInfo t0 = info.AudioTracks[0];
        Check("probe: track0 truehd metadata",
              t0.Ordinal == 0 && t0.Codec == "truehd (Dolby TrueHD + Dolby Atmos)" &&
              t0.Language == "eng" && t0.Channels == "7.1" && t0.SampleRate == 48000 &&
              t0.IsDefault && t0.Title == "TrueHD 7.1 Atmos");
        Check("probe: track3 stereo not multi-channel",
              info.AudioTracks[3].Channels == "stereo" && !info.AudioTracks[3].IsMultiChannel);
        Check("probe: track4 spanish", info.AudioTracks[4].Language == "spa");
        Check("probe: 7.1/5.1 flagged multi-channel",
              t0.IsMultiChannel && info.AudioTracks[2].IsMultiChannel);
        Check("probe: default audio track is 0", info.DefaultAudioTrack == 0);
        Check("probe: exceeds Hypseus 1080p limit", info.ExceedsHypseusLimit);
        Check("probe: display carries language+title+channels",
              t0.Display() == "[eng] TrueHD 7.1 Atmos — truehd (Dolby TrueHD + Dolby Atmos) · 7.1 · 48000 Hz (default)");

        // Default flag respected when it isn't the first track.
        MediaInfo alt = MediaInfo.Parse("""
            Stream #0:1(jpn): Audio: aac (LC), 44100 Hz, stereo, fltp
            Stream #0:2(eng): Audio: ac3, 48000 Hz, 5.1(side), fltp, 448 kb/s (default)
            """);
        Check("probe: default flag wins over order", alt.DefaultAudioTrack == 1);
        Check("probe: no-video probe has no video", !alt.HasVideo && !alt.ExceedsHypseusLimit);

        // mp4-style stream ids ([0x1], und language) parse too.
        MediaInfo mp4 = MediaInfo.Parse(
            "Stream #0:0[0x1](und): Video: h264 (High), yuv420p(progressive), 1920x1080, 30 fps, 30 tbr\n" +
            "Stream #0:1[0x2](und): Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s (default)\n");
        Check("probe: mp4 bracket ids parse",
              mp4 is { Width: 1920, Height: 1080, AudioTracks: [{ Language: "und", Channels: "stereo" }] });
        Check("probe: 1080p source within Hypseus limit", !mp4.ExceedsHypseusLimit);

        // Aspect-preserving downscale presets.
        Check("probe: 4K 16:9 presets",
              MediaInfo.DownscalePresets(3840, 2160).SequenceEqual(
                  [(1920, 1080), (1600, 900), (1280, 720), (1024, 576), (854, 480)]));
        Check("probe: 1080p source offers sub-1080 presets only",
              MediaInfo.DownscalePresets(1920, 1080).SequenceEqual(
                  [(1600, 900), (1280, 720), (1024, 576), (854, 480)]));
        Check("probe: 4:3 bottoms out at 640x480",
              MediaInfo.DownscalePresets(1440, 1080).SequenceEqual(
                  [(1200, 900), (960, 720), (768, 576), (640, 480)]));
        Check("probe: widths always even",
              MediaInfo.DownscalePresets(3996, 2160).All(p => p.Width % 2 == 0));
    }
}
