using System.Diagnostics;
using System.Security.Cryptography;
using Ldp.Engine;
using Ldp.EngineProbe;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

if (args.Length >= 1 && args[0] == "--project-test")
{
    return ProjectTest.Run();
}

if (args.Length >= 4 && args[0] == "--fill")
{
    // --fill <original.singe> <template.singe> <out.singe> [gamename]
    var fillProject = new Ldp.Project.LdpProject
    {
        Name = args.Length >= 5 ? args[4] : System.IO.Path.GetFileNameWithoutExtension(args[3]),
        Framework = Ldp.Project.GameFramework.StandardFramework,
    };
    Ldp.Project.SingeImporter.Result imp = Ldp.Project.SingeImporter.Import(fillProject, File.ReadAllText(args[1]));
    Console.WriteLine($"imported: {imp.Levels} levels, {imp.Scenes} scenes, {imp.Moves} moves, {imp.Deaths} deaths");
    foreach (string w in imp.Warnings) Console.WriteLine($"  import ⚠ {w}");
    Ldp.Project.SingeTemplate.Result fill = Ldp.Project.SingeTemplate.Apply(fillProject, File.ReadAllText(args[2]));
    File.WriteAllText(args[3], fill.Script);
    Console.WriteLine($"wrote {args[3]} ({fill.Script.Length} chars)");
    foreach (string w in fill.Warnings) Console.WriteLine($"  fill ⚠ {w}");
    return 0;
}

if (args.Length >= 2 && args[0] == "--resolve-check")
{
    Ldp.Project.LdpProject proj = Ldp.Project.ProjectFile.Load(args[1]);
    Console.WriteLine($"videos: {proj.Videos.Count}, scenes: {proj.Clips.Count}");
    foreach (Ldp.Project.VideoSource v in proj.Videos)
        Console.WriteLine($"  [{proj.Videos.IndexOf(v)}] {System.IO.Path.GetFileName(v.Path)}  " +
                          $"global {v.GlobalBase}..{v.GlobalBase + v.PictureCount - 1}");
    int bad = 0;
    foreach (Ldp.Project.Clip c in proj.Clips)
    {
        int vi = proj.VideoIndexOf(c.StartFrame);
        string where = vi < 0 ? "*** NO VIDEO ***" : System.IO.Path.GetFileName(proj.Videos[vi].Path);
        if (vi != 0) Console.WriteLine($"  scene '{c.Name}' {c.StartFrame}-{c.EndFrame} -> video[{vi}] {where}");
        if (vi < 0) bad++;
    }
    Console.WriteLine(bad == 0
        ? "every scene resolves to a video (non-video-0 scenes listed above)"
        : $"{bad} scenes resolve to no video");
    return 0;
}

if (args.Length >= 2 && args[0] == "--audio")
{
    FFmpegLogger.LogLevel = LogLevel.Error;
    using AudioTrack? track = AudioTrack.TryOpenFor(args[1]);
    if (track == null) { Console.WriteLine("no companion .ogg found / unreadable"); return 1; }
    Console.WriteLine($"audio    : {track.SampleRate} Hz, {track.Channels} ch");

    byte[] pcm = new byte[track.BytesPerSecond]; // 1 second
    static long Energy(byte[] b) { long e = 0; for (int i = 0; i < b.Length; i += 2) e += Math.Abs((short)(b[i] | (b[i + 1] << 8))); return e / (b.Length / 2); }

    var sw2 = Stopwatch.StartNew();
    track.Read(pcm, 0, pcm.Length);
    Console.WriteLine($"read 1s from start: {sw2.ElapsedMilliseconds} ms, mean |amp| {Energy(pcm)}");

    sw2.Restart();
    track.Seek(60.0);
    track.Read(pcm, 0, pcm.Length);
    Console.WriteLine($"seek 60s + read 1s: {sw2.ElapsedMilliseconds} ms, mean |amp| {Energy(pcm)}");

    sw2.Restart();
    track.Seek(10.0);
    track.Read(pcm, 0, pcm.Length);
    Console.WriteLine($"seek back 10s + read 1s: {sw2.ElapsedMilliseconds} ms, mean |amp| {Energy(pcm)}");
    return 0;
}

if (args.Length < 1)
{
    Console.WriteLine("usage: Ldp.EngineProbe <file.m2v> [--full] [--dat] [--dump <frame> <out.ppm>] | --project-test");
    return 1;
}

FFmpegLogger.LogLevel = LogLevel.Error;

string path = args[0];
bool full = args.Contains("--full");
bool writeDat = args.Contains("--dat");

Stopwatch sw = Stopwatch.StartNew();
FrameIndex idx = M2vScanner.Scan(path);
sw.Stop();

int iFrames = idx.DatEntries.Count(e => e >= 0);
Console.WriteLine($"scan     : {sw.ElapsedMilliseconds} ms ({idx.FileLength / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MB/s)");
Console.WriteLine($"stream   : {idx.Width}x{idx.Height} @ {idx.Fps:F3} fps (code {idx.FrameRateCode}), fields={idx.UsesFields}");
Console.WriteLine($"pictures : {idx.CodedPictureCount} coded ({iFrames} I-frames, {idx.GopCount} GOPs)");
Console.WriteLine($"frames   : {idx.FrameCount} (0..{idx.FrameCount - 1})");
Console.WriteLine($"seqcache : {idx.SequenceHeaderCache.Length} bytes before first GOP");

if (writeDat)
{
    string datPath = VldpDat.PathFor(path);
    VldpDat.Write(idx, datPath);
    Console.WriteLine($"dat      : wrote {new FileInfo(datPath).Length} bytes -> {datPath}");
}

using FrameEngine engine = FrameEngine.Open(path);

// Frame 0
sw.Restart();
FrameImage f0 = engine.GetFrame(0);
Console.WriteLine($"frame 0  : {sw.ElapsedMilliseconds} ms, {f0.Width}x{f0.Height}, md5 {Md5(f0)}");

// Random seek to the middle
int mid = idx.FrameCount / 2;
sw.Restart();
FrameImage fm = engine.GetFrame(mid);
Console.WriteLine($"frame {fm.FrameNumber}: seek {sw.ElapsedMilliseconds} ms, md5 {Md5(fm)}");

// Sequential step forward
sw.Restart();
FrameImage fn = engine.GetFrame(mid + 1);
Console.WriteLine($"frame {fn.FrameNumber}: step +1 {sw.ElapsedMilliseconds} ms");

// Step backward (should be cache hit from the seek's skip window, or re-seek)
sw.Restart();
FrameImage fb = engine.GetFrame(mid - 1);
Console.WriteLine($"frame {fb.FrameNumber}: step -1 {sw.ElapsedMilliseconds} ms");

// Determinism: seeking away and back must yield bit-identical pixels
string fmHash = Md5(fm);
engine.GetFrame(0);
FrameImage fm2 = engine.GetFrame(mid);
Console.WriteLine($"reseek {mid}: md5 {(Md5(fm2) == fmHash ? "MATCHES" : "*** MISMATCH ***")}");

if (full)
{
    // Decode the whole stream sequentially and verify the decoder emits
    // exactly one output per coded picture - the assumption VLDP's (and our)
    // skip counting rests on.
    using FrameEngine seq = FrameEngine.Open(path);
    sw.Restart();
    int count = 0;
    string? lastErr = null;
    try
    {
        for (int i = 0; i < seq.FrameCount; i++)
        {
            seq.GetFrame(i);
            count++;
        }
    }
    catch (Exception ex)
    {
        lastErr = ex.Message;
    }
    sw.Stop();
    Console.WriteLine($"full     : decoded {count}/{seq.FrameCount} sequentially in {sw.ElapsedMilliseconds} ms " +
                      $"({count / sw.Elapsed.TotalSeconds:F0} fps){(lastErr != null ? $" ERROR: {lastErr}" : "")}");

    // Cross-check: frames reached by cold seek must be bit-identical to the
    // same frames reached by sequential decode.
    using FrameEngine rnd = FrameEngine.Open(path);
    using FrameEngine seq2 = FrameEngine.Open(path);
    int mismatches = 0, checkedCount = 0;
    for (int i = 0; i < count; i += 97)
    {
        FrameImage a = seq2.GetFrame(i);
        string aHash = Md5(a);
        rnd.GetFrame(Math.Min(count - 1, i + 500)); // force the next call to be a cold seek
        FrameImage b = rnd.GetFrame(i);
        checkedCount++;
        if (aHash != Md5(b)) { mismatches++; Console.WriteLine($"  MISMATCH at frame {i}"); }
    }
    Console.WriteLine($"crosscheck: {checkedCount} frames, {mismatches} mismatches " +
                      (mismatches == 0 ? "(seek == sequential everywhere)" : "*** SEEK PATH IS NOT FRAME-EXACT ***"));
}

int dumpAt = Array.IndexOf(args, "--dump");
if (dumpAt >= 0 && args.Length > dumpAt + 2)
{
    int frameNo = int.Parse(args[dumpAt + 1]);
    string outPath = args[dumpAt + 2];
    FrameImage img = engine.GetFrame(frameNo);
    WritePpm(img, outPath);
    Console.WriteLine($"dump     : frame {frameNo} -> {outPath}");
}

return 0;

static string Md5(FrameImage f) => Convert.ToHexString(MD5.HashData(f.Bgra))[..16];

static void WritePpm(FrameImage f, string path)
{
    using FileStream fs = File.Create(path);
    fs.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{f.Width} {f.Height}\n255\n"));
    byte[] row = new byte[f.Width * 3];
    for (int y = 0; y < f.Height; y++)
    {
        for (int x = 0; x < f.Width; x++)
        {
            int s = y * f.Stride + x * 4;
            row[x * 3 + 0] = f.Bgra[s + 2];
            row[x * 3 + 1] = f.Bgra[s + 1];
            row[x * 3 + 2] = f.Bgra[s + 0];
        }
        fs.Write(row);
    }
}
