using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ldp.App;

/// <summary>Locates and runs the external FFmpeg used for video conversion. FFmpeg
/// is intentionally not bundled — the user points at ffmpeg.exe from an extracted
/// gyan.dev full build, and the path is remembered in <see cref="AppSettings"/>.</summary>
public static class FfmpegTool
{
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/";

    public static bool IsValidExe(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        Path.GetFileName(path).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort search of PATH so users who already have FFmpeg installed
    /// don't have to locate it by hand. Returns null if nothing is found.</summary>
    public static string? ProbeSystem()
    {
        try
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar == null) return null;
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception) { /* PATH probing is a convenience, never fatal */ }
        return null;
    }

    public sealed record RunResult(bool Ok, int ExitCode, string Tail);

    /// <summary>
    /// Runs one FFmpeg invocation, streaming its stderr (where FFmpeg writes progress)
    /// to <paramref name="onLine"/>, and a 0..1 fraction to <paramref name="onProgress"/>
    /// once the media duration is known. Callbacks fire on a background thread — the
    /// caller marshals to the UI. Cancelling the token kills the process tree.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        string ffmpegExe, IReadOnlyList<string> args,
        Action<string>? onLine, Action<double>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        TimeSpan? duration = null;

        proc.Start();
        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception) { }
        });

        // Drain stdout so its pipe can never fill and stall the process.
        Task stdoutDrain = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

        // FFmpeg rewrites the progress line with carriage returns, not newlines, so
        // split on both to surface live updates.
        var sb = new StringBuilder();
        char[] buf = new char[512];
        StreamReader err = proc.StandardError;
        int read;
        while ((read = await err.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buf[i];
                if (c is '\r' or '\n')
                {
                    if (sb.Length > 0) { EmitLine(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
        }
        if (sb.Length > 0) EmitLine(sb.ToString());

        try { await stdoutDrain.ConfigureAwait(false); } catch (Exception) { }
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        bool ok = proc.ExitCode == 0 && !ct.IsCancellationRequested;
        return new RunResult(ok, proc.ExitCode, string.Join(Environment.NewLine, tail));

        void EmitLine(string line)
        {
            onLine?.Invoke(line);
            if (tail.Count >= 12) tail.Dequeue();
            tail.Enqueue(line);

            duration ??= TryParse(DurationRx, line);
            if (duration is { TotalSeconds: > 0 } dur && TryParse(TimeRx, line) is { } cur)
                onProgress?.Invoke(Math.Clamp(cur.TotalSeconds / dur.TotalSeconds, 0, 1));
        }
    }

    private static readonly Regex DurationRx =
        new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRx =
        new(@"\btime=\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

    private static TimeSpan? TryParse(Regex rx, string line)
    {
        Match m = rx.Match(line);
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return new TimeSpan(0, h, min, (int)sec, (int)(sec % 1 * 1000));
    }
}
