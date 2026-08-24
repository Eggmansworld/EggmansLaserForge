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

        // Language tracks: each selected extra stream exports its own suffixed ogg
        // with the same audio settings, main .ogg untouched.
        FfmpegJob multi = FfmpegCommand.Build(input, new ConvertOptions(), dir, audioTrack: 0,
            languageTracks: [(6, "-fre", "French"), (4, "-spa", "Spanish")]);
        Check("ffmpeg: language jobs count", multi.LanguageAudio.Count == 2);
        Check("ffmpeg: language ogg naming",
            multi.LanguageAudio[0].OggPath == Path.Combine(dir, "clip-fre.ogg") &&
            multi.LanguageAudio[1].OggPath == Path.Combine(dir, "clip-spa.ogg"));
        Check("ffmpeg: language args map their own stream", multi.LanguageAudio[0].Args.SequenceEqual(
            ["-y", "-i", input, "-ss", "00:00:00.110", "-vn", "-c:a", "libvorbis",
             "-ar", "44100", "-map", "0:a:6", "-ac", "2", "-b:a", "160k", Path.Combine(dir, "clip-fre.ogg")]));
        Check("ffmpeg: language carries display name",
            multi.LanguageAudio[0].LanguageName == "French" && multi.LanguageAudio[0].Suffix == "-fre");
        Check("ffmpeg: main ogg unaffected by languages", multi.OggPath == ogg);
        Check("ffmpeg: no languages -> empty list",
            FfmpegCommand.Build(input, new ConvertOptions(), dir).LanguageAudio.Count == 0);
        Check("ffmpeg: audio off drops language jobs too",
            FfmpegCommand.Build(input, new ConvertOptions { CreateAudio = false }, dir,
                languageTracks: [(1, "-fre", "French")]).LanguageAudio.Count == 0);

        // Audio-only mode: no video pass, but the m2v path is still computed for
        // pairing, and audio + language jobs are built exactly as usual.
        FfmpegJob audioOnly = FfmpegCommand.Build(input, new ConvertOptions { AudioOnly = true }, dir,
            languageTracks: [(2, "-fre", "French")]);
        Check("ffmpeg: audio-only has no video args", audioOnly.VideoArgs.Count == 0);
        Check("ffmpeg: audio-only keeps m2v path for pairing", audioOnly.M2vPath == m2v);
        Check("ffmpeg: audio-only still builds main ogg args",
            audioOnly.AudioArgs != null && audioOnly.OggPath == ogg);
        Check("ffmpeg: audio-only still builds language jobs",
            audioOnly.LanguageAudio is [{ Suffix: "-fre" }]);
        Check("ffmpeg: normal mode still has video args",
            FfmpegCommand.Build(input, new ConvertOptions(), dir).VideoArgs.Count > 0);

        RunMediaInfo(Check);
        RunChaptersAndLanguages(Check);
        BlankChecks(Check, dir);
        FrameRateChecks(Check, dir);
        ColorChecks(Check, dir);
    }

    /// <summary>
    /// HDR sources. A 4K HDR master carries its brightness on the PQ curve and
    /// its colours on the BT.2020 triangle; MPEG-2 is 8-bit BT.709 SDR and
    /// Hypseus plays it as such. Encoding the samples unchanged keeps every
    /// number and changes what every number means, which is why TronLegacy.m2v
    /// came out "subdued, almost greyish, with very little colour" — measured at
    /// SATAVG 5.51 against 8.55 for the same frame tone-mapped.
    ///
    /// The stream lines below are FFmpeg's own, verbatim.
    /// </summary>
    private static void ColorChecks(Action<string, bool> Check, string dir)
    {
        MediaInfo hdr = MediaInfo.Parse(
            "  Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), " +
            "3840x2160 [SAR 1:1 DAR 16:9], 23.98 fps, 23.98 tbr, 1k tbn (default)");
        Check("color: HDR10 is detected", hdr.IsHdr && hdr.IsWideGamut);
        Check("color: HDR10 reads its descriptor", hdr.ColorInfo == "tv, bt2020nc/bt2020/smpte2084");
        Check("color: HDR10 asks to be tone-mapped",
              hdr.SuggestedColorConversion == ColorConversion.HdrToneMap);
        Check("color: the rest of the line still parses",
              hdr is { Width: 3840, Height: 2160, VideoCodec: "hevc (Main 10)" } && hdr.Fps == 23.98);

        MediaInfo hlg = MediaInfo.Parse(
            "  Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/arib-std-b67), 3840x2160");
        Check("color: HLG counts as HDR too", hlg.IsHdr);

        // FFmpeg collapses the descriptor to one value when all three agree, so
        // an ordinary SDR file must not look like it needs anything doing.
        MediaInfo sdr = MediaInfo.Parse(
            "  Stream #0:0: Video: h264 (High), yuv420p(tv, bt709), 1920x1080 [SAR 1:1 DAR 16:9], 23.98 fps");
        Check("color: plain BT.709 needs nothing",
              !sdr.IsHdr && !sdr.IsWideGamut && sdr.SuggestedColorConversion == ColorConversion.None);

        MediaInfo bare = MediaInfo.Parse("  Stream #0:0: Video: mpeg2video (Main), yuv420p, 720x480, 29.97 fps");
        Check("color: a file that declares nothing needs nothing",
              bare.ColorInfo.Length == 0 && bare.SuggestedColorConversion == ColorConversion.None);

        // Wide gamut without HDR: the gamut alone is wrong, no tone-map needed.
        MediaInfo wide = MediaInfo.Parse(
            "  Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/bt709), 3840x2160");
        Check("color: wide-gamut SDR converts the gamut only",
              !wide.IsHdr && wide.SuggestedColorConversion == ColorConversion.GamutOnly);

        // ---- The filter chain ----
        Check("color: no conversion and no scale means no filter at all",
              FfmpegCommand.VideoFilter(null, ColorConversion.None) == null);
        Check("color: scale alone is untouched",
              FfmpegCommand.VideoFilter((1920, 1080), ColorConversion.None) == "scale=1920:1080");

        string chain = FfmpegCommand.VideoFilter((1920, 1080), ColorConversion.HdrToneMap)!;
        Check("color: tone-map chain exact", chain ==
              "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709," +
              "tonemap=tonemap=hable:desat=0,zscale=w=1920:h=1080:t=bt709:m=bt709:r=tv,format=yuv420p");
        // The resize belongs inside the linear stage: scaling after the transfer
        // curve is back on, in 8-bit, throws away what the tone-map recovered.
        Check("color: the resize happens before the output transfer",
              chain.IndexOf("w=1920:h=1080", StringComparison.Ordinal) <
              chain.IndexOf("format=yuv420p", StringComparison.Ordinal) &&
              !chain.Contains("scale=1920:1080"));
        Check("color: tonemap runs on float RGB",
              chain.IndexOf("format=gbrpf32le", StringComparison.Ordinal) <
              chain.IndexOf("tonemap=", StringComparison.Ordinal));
        // desat defaults ON and pulls highlights toward white, which is the very
        // thing this conversion exists to stop.
        Check("color: highlight desaturation is off", chain.Contains("desat=0"));
        Check("color: tone-mapping without a downscale keeps the source size",
              FfmpegCommand.VideoFilter(null, ColorConversion.HdrToneMap) ==
              "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709," +
              "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        Check("color: gamut-only skips the linear round trip",
              FfmpegCommand.VideoFilter((1920, 1080), ColorConversion.GamutOnly) ==
              "zscale=w=1920:h=1080:p=bt709:t=bt709:m=bt709:r=tv,format=yuv420p" &&
              !FfmpegCommand.VideoFilter(null, ColorConversion.GamutOnly)!.Contains("tonemap"));

        // ---- The built job ----
        string input = System.IO.Path.Combine(dir, "TronLegacy.mkv");
        IReadOnlyList<string> args = FfmpegCommand
            .Build(input, new ConvertOptions(), dir, 0, (1920, 1080), null, ColorConversion.HdrToneMap)
            .VideoArgs;
        Check("color: the job carries the tone-map filter",
              PositionOf(args, "-vf") >= 0 && args[PositionOf(args, "-vf") + 1] == chain);
        // Without these the .m2v inherits the source's BT.2020/PQ description and
        // claims to be HDR while holding SDR pixels — exactly how the file the
        // app produced today is tagged.
        Check("color: the output is tagged BT.709",
              PositionOf(args, "-color_primaries") >= 0 && PositionOf(args, "-color_trc") >= 0 &&
              PositionOf(args, "-colorspace") >= 0);
        Check("color: the tags come before the output file",
              PositionOf(args, "-colorspace") < args.Count - 1 &&
              args[^1] == System.IO.Path.Combine(dir, "TronLegacy.m2v"));
        Check("color: an SDR job gains no tags and no chain",
              FfmpegCommand.Build(input, new ConvertOptions(), dir, 0, (1920, 1080)).VideoArgs
                  is var plain &&
              PositionOf(plain, "-colorspace") < 0 &&
              plain[PositionOf(plain, "-vf") + 1] == "scale=1920:1080");
        Check("color: audio-only mode builds no video pass at all",
              FfmpegCommand.Build(input, new ConvertOptions { AudioOnly = true }, dir, 0, null, null,
                                  ColorConversion.HdrToneMap).VideoArgs.Count == 0);
    }

    /// <summary>Chapter parsing + chapter→scene math + language-code helpers.</summary>
    private static void RunChaptersAndLanguages(Action<string, bool> Check)
    {
        // Chapter lines exactly as the real Alita dump prints them.
        MediaInfo info = MediaInfo.Parse("""
              Duration: 02:01:57.38, start: 0.000000, bitrate: 62567 kb/s
              Chapters:
                Chapter #0:0: start 0.000000, end 208.708500
                  Metadata:
                    title           : Chapter 01
                Chapter #0:1: start 208.708500, end 471.679542
                  Metadata:
                    title           : Chapter 02
                Chapter #0:2: start 471.679542, end 607.648708
              Stream #0:0(eng): Video: hevc (Main 10), yuv420p10le, 3840x2160 [SAR 1:1 DAR 16:9], 23.98 fps
              Stream #0:1(eng): Audio: truehd, 48000 Hz, 7.1, s32 (24 bit) (default)
                Metadata:
                  title           : TrueHD 7.1 Atmos
            """);
        Check("chapters: three parsed", info.Chapters.Count == 3);
        Check("chapters: numbered from 1",
            info.Chapters[0].Number == 1 && info.Chapters[2].Number == 3);
        Check("chapters: times parsed",
            Math.Abs(info.Chapters[1].StartSeconds - 208.7085) < 0.0001 &&
            Math.Abs(info.Chapters[1].EndSeconds - 471.679542) < 0.0001);
        Check("chapters: chapter title not mistaken for audio title",
            info.AudioTracks is [{ Title: "TrueHD 7.1 Atmos" }]);

        // Chapters → scenes: consecutive, gap-free, zero-padded names so a long
        // import stays in order wherever the names are shown plainly.
        List<Clip> scenes = ChapterImport.BuildScenes(info.Chapters, 23.98, globalBase: 1000, pictureCount: 200000);
        Check("chapters: one scene per chapter", scenes.Count == 3);
        Check("chapters: standardized padded names",
            scenes[0].Name == "Chapter 01 (imported)" && scenes[2].Name == "Chapter 03 (imported)");
        Check("chapters: scene 1 spans to chapter 2 start",
            scenes[0].StartFrame == 1000 && scenes[0].EndFrame == 1000 + 5005 - 1);
        Check("chapters: scenes are contiguous",
            scenes[1].StartFrame == scenes[0].EndFrame + 1 && scenes[2].StartFrame == scenes[1].EndFrame + 1);
        Check("chapters: last scene ends at its own end time",
            scenes[2].EndFrame == 1000 + (int)Math.Round(607.648708 * 23.98) - 1);

        // Clamping: a chapter past the video's end is clamped; degenerate dropped.
        List<Clip> clamped = ChapterImport.BuildScenes(
            [new ChapterInfo(1, 0, 10), new ChapterInfo(2, 10, 10.001), new ChapterInfo(3, 10.001, 99999)],
            fps: 30, globalBase: 0, pictureCount: 600);
        // A dropped chapter must not shift the numbering of the ones that
        // survive, and must not change the padding width either.
        Check("chapters: degenerate chapter skipped, numbering kept",
            clamped.Count == 2 && clamped[1].Name == "Chapter 03 (imported)");
        Check("chapters: clamped to video end", clamped[1].EndFrame == 599);

        // Language code helpers.
        Check("lang: display names", LanguageCodes.DisplayName("fre") == "French" &&
            LanguageCodes.DisplayName("spa") == "Spanish" && LanguageCodes.DisplayName("und") == "Unknown" &&
            LanguageCodes.DisplayName("xyz") == "Xyz");
        var freTrack = new AudioTrackInfo(3, "dts", "fre", "5.1(side)", 48000, false, "DTS 5.1");
        var untagged = new AudioTrackInfo(2, "aac", "", "stereo", 44100, false, "");
        Check("lang: suffix from code", LanguageCodes.SuffixFor(freTrack) == "-fre");
        Check("lang: untagged falls back to ordinal", LanguageCodes.SuffixFor(untagged) == "-a3");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Check("lang: dedupe -spa/-spa2",
            LanguageCodes.Unique("-spa", taken) == "-spa" && LanguageCodes.Unique("-spa", taken) == "-spa2");
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

    private static int PositionOf(IReadOnlyList<string> args, string token)
    {
        for (int i = 0; i < args.Count; i++)
            if (args[i] == token) return i;
        return -1;
    }

    /// <summary>
    /// Blanking a span of frames. Every frame number an imported game holds is an
    /// index into its video file, so the one thing this must never do is shift
    /// them: the span is inclusive at both ends and the encode is frame-for-frame.
    /// </summary>
    private static void BlankChecks(Action<string, bool> Check, string dir)
    {
        string src = Path.Combine(dir, "main.m2v");
        string dst = Path.Combine(dir, "main-blanked.m2v");
        var balanced = new ConvertOptions { Video = VideoQuality.Balanced };

        // between() is inclusive on BOTH ends. "blank 0 through K-1" is therefore
        // between(n,0,K-1) - writing between(n,0,K) blanks one frame too many,
        // and that frame could be the first one a death scene needs.
        Check("blank: filter is inclusive at both ends",
              FfmpegCommand.BlankFilter(new FfmpegCommand.BlankSpan(0, 49648)) ==
              "drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='between(n\\,0\\,49648)'");
        Check("blank: single frame span",
              FfmpegCommand.BlankFilter(new FfmpegCommand.BlankSpan(7, 7)).Contains("between(n\\,7\\,7)"));

        Check("blank: span counts inclusively",
              new FfmpegCommand.BlankSpan(0, 49648).FrameCount == 49649 &&
              new FfmpegCommand.BlankSpan(100, 100).FrameCount == 1);
        Check("blank: backwards and negative spans are invalid",
              !new FfmpegCommand.BlankSpan(500, 499).IsValid &&
              !new FfmpegCommand.BlankSpan(-1, 10).IsValid &&
              new FfmpegCommand.BlankSpan(0, 0).IsValid);

        // Same quality flags as every other pass, plus passthrough so the frame
        // count survives - an output -r would resample against the rate FFmpeg
        // infers from the sequence header and silently drop frames.
        IReadOnlyList<string> args = FfmpegCommand.BlankArgs(
            src, dst, new FfmpegCommand.BlankSpan(0, 49648), balanced);
        Check("blank: args exact", args.SequenceEqual(
            ["-y", "-i", src,
             "-vf", "drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='between(n\\,0\\,49648)'",
             "-an", "-qscale:v", "4", "-b:v", "6000k",
             "-fps_mode", "passthrough", "-codec:v", "mpeg2video", dst]));
        Check("blank: never carries an output -r", !args.Skip(3).Contains("-r"));
        Check("blank: honours the quality preset",
              FfmpegCommand.BlankArgs(src, dst, new FfmpegCommand.BlankSpan(0, 5), new ConvertOptions())
                  .Contains("1"));
    }

    /// <summary>
    /// Re-timing a clip to the project's frame rate. Unlike blanking, this is
    /// meant to change the frame count — it is a real re-timing so the clip plays
    /// at the right speed rather than 20% fast.
    /// </summary>
    private static void FrameRateChecks(Action<string, bool> Check, string dir)
    {
        _ = dir;
        string src = Path.Combine(dir, "clip25.m2v");
        string dst = Path.Combine(dir, "clip25-29.97.m2v");

        // The broadcast rates are ratios. 29.97 drifts about a frame every two
        // minutes against 30000/1001, which over a feature is minutes of desync.
        Check("fps: NTSC rates written as exact ratios",
              FfmpegCommand.FormatFps(29.97) == "30000/1001" &&
              FfmpegCommand.FormatFps(30000.0 / 1001) == "30000/1001" &&
              FfmpegCommand.FormatFps(23.976) == "24000/1001" &&
              FfmpegCommand.FormatFps(59.94) == "60000/1001");
        Check("fps: whole rates stay whole",
              FfmpegCommand.FormatFps(25) == "25" && FfmpegCommand.FormatFps(24) == "24" &&
              FfmpegCommand.FormatFps(30) == "30" && FfmpegCommand.FormatFps(50) == "50");
        Check("fps: an odd rate falls back to a decimal",
              FfmpegCommand.FormatFps(18.5) == "18.5");

        // -r BEFORE -i declares the source rate; the fps filter resamples from it.
        // ffprobe reports 25 fps for a raw .m2v that is really 29.97, so the rate
        // has to be told, not guessed.
        IReadOnlyList<string> args = FfmpegCommand.FrameRateArgs(
            src, dst, 25, 29.97, new ConvertOptions { Video = VideoQuality.Balanced });
        Check("fps: args exact", args.SequenceEqual(
            ["-y", "-r", "25", "-i", src, "-vf", "fps=30000/1001", "-an",
             "-qscale:v", "4", "-b:v", "6000k", "-codec:v", "mpeg2video", dst]));
        Check("fps: source rate is declared before the input",
              PositionOf(args, "-r") >= 0 && PositionOf(args, "-r") < PositionOf(args, "-i"));

        // 1110 frames of 29.97 became 926 at 25 in the real conversion.
        Check("fps: predicted count matches the measured conversion",
              FfmpegCommand.FrameCountAfterRateChange(1110, 29.97, 25) == 926);
        Check("fps: predicted count going up",
              FfmpegCommand.FrameCountAfterRateChange(1110, 25, 29.97) == 1331);
        Check("fps: same rate is a no-op count",
              FfmpegCommand.FrameCountAfterRateChange(5000, 29.97, 29.97) == 5000);
        Check("fps: degenerate inputs give zero",
              FfmpegCommand.FrameCountAfterRateChange(0, 25, 30) == 0 &&
              FfmpegCommand.FrameCountAfterRateChange(100, 0, 30) == 0);
    }
}
