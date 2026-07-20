using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Exact-argument checks for the FFmpeg command builder. These lock the generated
/// command lines to the author's proven script (mpeg2video video with the qscale
/// presets; libvorbis audio with the 44.1 kHz / -map a / -ss offset shape) so a
/// refactor can't silently change what gets run.
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
        FfmpegJob hi = FfmpegCommand.Build(input, new ConvertOptions(), dir);
        Check("ffmpeg: highest video args exact", hi.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-an", "-qscale:v", "1", "-codec:v", "mpeg2video", m2v]));
        Check("ffmpeg: m2v path beside source", hi.M2vPath == m2v);
        Check("ffmpeg: standard audio args exact", hi.AudioArgs!.SequenceEqual(
            ["-y", "-i", input, "-ss", "00:00:00.110", "-vn", "-c:a", "libvorbis",
             "-ar", "44100", "-map", "a", "-b:a", "160k", ogg]));
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

        // Resize inserts -vf scale immediately after the input, before -an.
        FfmpegJob rez = FfmpegCommand.Build(input,
            new ConvertOptions { Resize = true, Width = 1920, Height = 1080 }, dir);
        Check("ffmpeg: resize adds -vf scale", rez.VideoArgs.SequenceEqual(
            ["-y", "-i", input, "-vf", "scale=1920:1080", "-an", "-qscale:v", "1", "-codec:v", "mpeg2video", m2v]));

        // Custom audio bitrate + a different sync offset.
        FfmpegJob a320 = FfmpegCommand.Build(input,
            new ConvertOptions { Audio = AudioQuality.Custom, CustomAudioBitrateK = 320, AudioOffsetMs = 250 }, dir);
        Check("ffmpeg: custom audio bitrate + offset", a320.AudioArgs!.SequenceEqual(
            ["-y", "-i", input, "-ss", "00:00:00.250", "-vn", "-c:a", "libvorbis",
             "-ar", "44100", "-map", "a", "-b:a", "320k", ogg]));

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
    }
}
