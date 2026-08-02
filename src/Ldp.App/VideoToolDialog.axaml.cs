using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ldp.Engine;
using Ldp.Project;

namespace Ldp.App;

/// <summary>Which repair this dialog is set up for.</summary>
public enum VideoToolMode
{
    /// <summary>Paint a span of frames black, leaving every frame number in place.</summary>
    Blank,

    /// <summary>Re-time a clip to the project's frame rate.</summary>
    FrameRate,
}

/// <summary>
/// Two one-shot repairs on an .m2v that don't belong in the main conversion
/// dialog, because neither is "turn a source video into a game video".
///
/// <b>Blank</b> exists for imported games whose author chopped the feature film
/// into out-of-order pieces and appended the deaths, stills and system videos at
/// the end. Once the film is available as its own clean video, the film half is
/// dead weight — but its frame NUMBERS are still load-bearing, because every
/// death and slot in the imported script indexes into that same file. Painting
/// those frames black keeps the numbering and drops most of the bytes.
///
/// <b>FrameRate</b> exists because every video in a game must share one rate —
/// all move timing is counted in frames — so a clip that arrives at 25 fps
/// cannot join a 29.97 project until it is re-timed.
/// </summary>
public partial class VideoToolDialog : Window
{
    private readonly AppSettings _settings;
    private readonly VideoToolMode _mode;
    private readonly LdpProject? _project;
    private readonly string? _projectPath;

    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>Project videos offered for blanking, in project order.</summary>
    private readonly List<(VideoSource Video, string Path)> _videos = [];

    /// <summary>The file chosen in FrameRate mode, with what the engine read off it.</summary>
    private string? _pickedPath;
    private int _pickedFrames;
    private double _pickedFps;

    /// <summary>Frame rates offered, project rate first.</summary>
    private readonly List<double> _fpsChoices = [];

    /// <summary>Set when a run finished and wrote a file.</summary>
    public string? ProducedPath { get; private set; }

    /// <summary>In Blank mode, which project video the produced file replaces.</summary>
    public VideoSource? BlankedVideo { get; private set; }

    /// <summary>In Blank mode, the span that was painted black — the caller needs
    /// it to throw away the cached pictures that still show the old footage.</summary>
    public FfmpegCommand.BlankSpan? BlankedSpan { get; private set; }

    /// <summary>In FrameRate mode, the rate the produced file was written at.</summary>
    public double ProducedFps { get; private set; }

    public VideoToolDialog()
    {
        _settings = new AppSettings();
        InitializeComponent();
    }

    public VideoToolDialog(AppSettings settings, VideoToolMode mode,
                           LdpProject? project, string? projectPath) : this()
    {
        _settings = settings;
        _mode = mode;
        _project = project;
        _projectPath = projectPath;

        if (mode == VideoToolMode.Blank) SetUpBlank();
        else SetUpFrameRate();

        if (FfmpegTool.IsValidExe(_settings.FfmpegPath)) SetFfmpeg(_settings.FfmpegPath!, persist: false);
        else if (FfmpegTool.ProbeSystem() is { } found) SetFfmpeg(found, persist: true);

        UpdateEverything();
    }

    // ---------- Mode set-up ----------

    private void SetUpBlank()
    {
        Title = "Black Out Frames";
        HeadlineText.Text = "Black out a span of frames";
        BlurbText.Text =
            "Replaces the picture across a span with black, without moving a single frame. " +
            "Use it when part of a video is no longer needed but the frame numbers after it are — " +
            "an imported game's deaths and still slots point into the same file as its footage.";
        SourceHeader.Text = "VIDEO  (from this project)";
        VideoCombo.IsVisible = true;
        BlankPanel.IsVisible = true;

        if (_project == null || _projectPath == null) return;
        foreach (VideoSource v in _project.Videos)
            _videos.Add((v, ProjectFile.ResolveVideoPath(_projectPath, v)));
        VideoCombo.ItemsSource = _videos
            .Select(v => $"{System.IO.Path.GetFileName(v.Path)}   ({v.Video.PictureCount} frames)")
            .ToList();
        VideoCombo.PlaceholderText = _videos.Count > 0
            ? "Choose a video…"
            : "This project has no videos yet.";
        if (_videos.Count == 1) VideoCombo.SelectedIndex = 0;
    }

    private void SetUpFrameRate()
    {
        Title = "Change Frame Rate";
        HeadlineText.Text = "Change a clip's frame rate";
        BlurbText.Text =
            "Every video in a game has to share one frame rate, because all move timing is counted " +
            "in frames. This re-times a clip that arrived at the wrong one so it can join the project.";
        SourceHeader.Text = "CLIP TO CONVERT  (.m2v, not yet in the project)";
        PickFilePanel.IsVisible = true;
        FpsPanel.IsVisible = true;

        // The project's own rate goes first and is preselected — matching it is
        // the entire reason to run this.
        if (_project is { Videos.Count: > 0 }) _fpsChoices.Add(_project.Videos[0].Fps);
        foreach (double rate in new[] { 30000.0 / 1001, 25, 24000.0 / 1001, 24, 30, 60000.0 / 1001, 50, 60 })
            if (!_fpsChoices.Any(f => Math.Abs(f - rate) < 0.005)) _fpsChoices.Add(rate);

        TargetFpsCombo.ItemsSource = _fpsChoices
            .Select((f, i) => $"{f:0.###} fps" + (i == 0 && _project is { Videos.Count: > 0 }
                ? "   — this project's rate"
                : ""))
            .ToList();
        TargetFpsCombo.SelectedIndex = 0;
    }

    // ---------- FFmpeg ----------

    private void SetFfmpeg(string path, bool persist)
    {
        FfmpegStatus.Foreground = (IBrush?)this.FindResource("FgMuted");
        FfmpegStatus.Text = "✓ " + path;
        if (!persist) return;
        _settings.FfmpegPath = path;
        _settings.Save();
    }

    private async void OnLocateFfmpeg(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate ffmpeg.exe (inside the extracted build's bin\\ folder)",
            FileTypeFilter =
            [
                new FilePickerFileType("ffmpeg.exe") { Patterns = ["ffmpeg.exe"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        string? path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;

        if (!FfmpegTool.IsValidExe(path))
        {
            FfmpegStatus.Foreground = (IBrush?)this.FindResource("PortDeath");
            FfmpegStatus.Text = "That isn't ffmpeg.exe — pick the file named ffmpeg.exe in the build's bin\\ folder.";
            return;
        }
        SetFfmpeg(path, persist: true);
        UpdateEverything();
    }

    private void OnOpenFfmpegDownload(object? sender, PointerPressedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = FfmpegTool.DownloadUrl, UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ---------- Source selection ----------

    private void OnVideoChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedVideo is { } picked)
        {
            // Default to the whole file so the numbers are obviously in range,
            // and so "from the start" only needs its one end filling in.
            int last = Math.Max(0, picked.Video.PictureCount - 1);
            StartModeLastBox.Maximum = last;
            RangeFirstBox.Maximum = last;
            RangeLastBox.Maximum = last;
        }
        UpdateEverything();
    }

    private (VideoSource Video, string Path)? SelectedVideo =>
        VideoCombo.SelectedIndex >= 0 && VideoCombo.SelectedIndex < _videos.Count
            ? _videos[VideoCombo.SelectedIndex]
            : null;

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the .m2v to re-time",
            FileTypeFilter =
            [
                new FilePickerFileType("MPEG-2 elementary stream (*.m2v)") { Patterns = ["*.m2v", "*.mpv"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;

        _pickedPath = path;
        PickedFileText.Text = System.IO.Path.GetFileName(path);
        SourceInfo.Text = "Reading the file…";
        SourceWarning.IsVisible = false;
        RunButton.IsEnabled = false;

        // The rate has to come from the sequence header: ffprobe reports 25 fps
        // for a raw .m2v that is really 29.97, and getting it wrong here would
        // re-time the clip by exactly the wrong ratio.
        try
        {
            FrameEngine engine = await Task.Run(() => FrameEngine.Open(path));
            _pickedFps = engine.Fps;
            _pickedFrames = engine.Index.CodedPictureCount;
            engine.Dispose();
        }
        catch (Exception ex)
        {
            _pickedPath = null;
            SourceInfo.Text = "";
            ShowWarning("Could not read that file: " + ex.Message);
            UpdateEverything();
            return;
        }

        UpdateEverything();
    }

    // ---------- Options ----------

    private void OnSpanModeChanged(object? sender, RoutedEventArgs e)
    {
        bool range = RangeRadio.IsChecked == true;
        StartModeLastBox.IsEnabled = !range;
        RangeFirstBox.IsEnabled = range;
        RangeLastBox.IsEnabled = range;
        UpdateEverything();
    }

    private void OnSpanValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateEverything();

    private void OnTargetFpsChanged(object? sender, SelectionChangedEventArgs e) => UpdateEverything();

    private double TargetFps =>
        TargetFpsCombo.SelectedIndex >= 0 && TargetFpsCombo.SelectedIndex < _fpsChoices.Count
            ? _fpsChoices[TargetFpsCombo.SelectedIndex]
            : 0;

    /// <summary>The span the boxes currently describe, both ends inclusive.</summary>
    private FfmpegCommand.BlankSpan CurrentSpan =>
        RangeRadio.IsChecked == true
            ? new FfmpegCommand.BlankSpan((int)(RangeFirstBox.Value ?? 0), (int)(RangeLastBox.Value ?? 0))
            : new FfmpegCommand.BlankSpan(0, (int)(StartModeLastBox.Value ?? 0));

    // ---------- Recompute ----------

    private void UpdateEverything()
    {
        if (_running) return;
        SourceWarning.IsVisible = false;
        bool ready = FfmpegTool.IsValidExe(_settings.FfmpegPath);

        if (_mode == VideoToolMode.Blank) ready = UpdateBlank() && ready;
        else ready = UpdateFrameRate() && ready;

        RunButton.IsEnabled = ready;
    }

    private bool UpdateBlank()
    {
        if (SelectedVideo is not { } picked)
        {
            SourceInfo.Text = "";
            SpanSummary.Text = "";
            OutputPathText.Text = "";
            CommandBox.Text = "";
            return false;
        }

        int total = picked.Video.PictureCount;
        SourceInfo.Text = $"{total} frames · file frames 0 – {total - 1} · " +
                          $"global {picked.Video.GlobalBase} – {picked.Video.GlobalBase + total - 1}";

        FfmpegCommand.BlankSpan span = CurrentSpan;
        if (!span.IsValid)
        {
            SpanSummary.Text = "";
            ShowWarning("The last frame has to be the same as, or after, the first.");
            return false;
        }
        if (span.LastFrame > total - 1)
        {
            SpanSummary.Text = "";
            ShowWarning($"This video only has {total} frames, so its last frame is {total - 1}.");
            return false;
        }

        int kept = total - span.FrameCount;
        SpanSummary.Text =
            $"{span.FrameCount:N0} of {total:N0} frames blacked out ({span.FrameCount * 100.0 / total:0.#}%), " +
            $"{kept:N0} left as they are. Frames stay at {span.FirstFrame}–{span.LastFrame}; " +
            $"in game frames that is {picked.Video.GlobalBase + span.FirstFrame}–{picked.Video.GlobalBase + span.LastFrame}.";

        string output = BlankOutputPath(picked.Path);
        OutputPathText.Text = output;
        CommandBox.Text = FfmpegCommand.Display(
            _settings.FfmpegPath ?? "ffmpeg",
            FfmpegCommand.BlankArgs(picked.Path, output, span, ReleaseQuality));
        return true;
    }

    private bool UpdateFrameRate()
    {
        if (_pickedPath == null)
        {
            SourceInfo.Text = "";
            FpsSummary.Text = "";
            OutputPathText.Text = "";
            CommandBox.Text = "";
            return false;
        }

        SourceInfo.Text = $"{_pickedFrames:N0} frames at {_pickedFps:0.###} fps · " +
                          $"{TimeSpan.FromSeconds(_pickedFrames / Math.Max(_pickedFps, 0.001)):hh\\:mm\\:ss}";

        double target = TargetFps;
        if (target <= 0) return false;

        if (Math.Abs(target - _pickedFps) < 0.005)
        {
            FpsSummary.Text = "";
            ShowWarning($"This clip is already {_pickedFps:0.###} fps — there is nothing to convert.");
            return false;
        }

        // Re-timing a video the project already holds would move every frame
        // number in it, which is exactly the damage this tool exists to avoid
        // elsewhere.
        if (_project != null && _projectPath != null &&
            _project.Videos.Any(v => PathsMatch(ProjectFile.ResolveVideoPath(_projectPath, v), _pickedPath)))
        {
            FpsSummary.Text = "";
            ShowWarning("That file is already one of this project's videos. Re-timing changes the frame " +
                        "count, which would move every scene, move and slot in it. Convert a copy instead.");
            return false;
        }

        int after = FfmpegCommand.FrameCountAfterRateChange(_pickedFrames, _pickedFps, target);
        FpsSummary.Text = $"{_pickedFrames:N0} frames at {_pickedFps:0.###} fps → {after:N0} frames at " +
                          $"{target:0.###} fps. Same running time; " +
                          (after > _pickedFrames ? $"{after - _pickedFrames:N0} frames added."
                                                 : $"{_pickedFrames - after:N0} frames dropped.");

        string output = FpsOutputPath(_pickedPath, target);
        OutputPathText.Text = output;
        CommandBox.Text = FfmpegCommand.Display(
            _settings.FfmpegPath ?? "ffmpeg",
            FfmpegCommand.FrameRateArgs(_pickedPath, output, _pickedFps, target, ReleaseQuality));
        return true;
    }

    private void ShowWarning(string text)
    {
        SourceWarning.Text = text;
        SourceWarning.IsVisible = true;
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>The release preset — these outputs go straight into a finished game.</summary>
    private static ConvertOptions ReleaseQuality => new()
    {
        Video = VideoQuality.Balanced,
        AudioOnly = false,
        CreateAudio = false,
    };

    private static string BlankOutputPath(string source) =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(source)) ?? ".",
            System.IO.Path.GetFileNameWithoutExtension(source) + "-blanked.m2v");

    private static string FpsOutputPath(string source, double fps) =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(source)) ?? ".",
            $"{System.IO.Path.GetFileNameWithoutExtension(source)}-{fps:0.###}fps.m2v");

    // ---------- Run ----------

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            return;
        }
        if (!FfmpegTool.IsValidExe(_settings.FfmpegPath)) return;

        string input, output;
        IReadOnlyList<string> args;
        if (_mode == VideoToolMode.Blank)
        {
            if (SelectedVideo is not { } picked) return;
            input = picked.Path;
            output = BlankOutputPath(input);
            args = FfmpegCommand.BlankArgs(input, output, CurrentSpan, ReleaseQuality);
            BlankedVideo = picked.Video;
            BlankedSpan = CurrentSpan;
        }
        else
        {
            if (_pickedPath == null) return;
            input = _pickedPath;
            output = FpsOutputPath(input, TargetFps);
            args = FfmpegCommand.FrameRateArgs(input, output, _pickedFps, TargetFps, ReleaseQuality);
            ProducedFps = TargetFps;
        }

        // Never write over the file being read: FFmpeg would truncate it first
        // and the source would be gone.
        if (PathsMatch(input, output))
        {
            ShowWarning("The output would overwrite the source. Rename one of them first.");
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        ProgressArea.IsVisible = true;
        RunProgress.Value = 0;
        OutputLog.Text = "";
        ProgressStatus.Text = $"Encoding {System.IO.Path.GetFileName(output)}…";
        RunButton.Content = "■ Stop";
        CloseButton.IsEnabled = false;
        SetOptionsEnabled(false);

        FfmpegTool.RunResult result;
        try
        {
            result = await FfmpegTool.RunAsync(
                _settings.FfmpegPath!, args,
                line => Dispatcher.UIThread.Post(() => AppendOutput(line)),
                p => Dispatcher.UIThread.Post(() => RunProgress.Value = p),
                _cts.Token);
        }
        catch (Exception ex)
        {
            result = new FfmpegTool.RunResult(false, -1, ex.Message);
        }

        _running = false;
        RunButton.Content = "▶ Run";
        CloseButton.IsEnabled = true;
        SetOptionsEnabled(true);

        if (result.Ok && File.Exists(output))
        {
            ProducedPath = output;
            RunProgress.Value = 1;
            ProgressStatus.Text = $"Done — {System.IO.Path.GetFileName(output)} " +
                                  $"({new FileInfo(output).Length / 1024.0 / 1024:N1} MB). Close to carry on.";
            RunButton.IsEnabled = false;
        }
        else
        {
            ProducedPath = null;
            BlankedVideo = null;
            ProgressStatus.Text = _cts.IsCancellationRequested
                ? "Stopped."
                : $"FFmpeg failed (exit {result.ExitCode}).";
            AppendOutput(result.Tail);
            UpdateEverything();
        }
    }

    private void SetOptionsEnabled(bool on)
    {
        VideoCombo.IsEnabled = on;
        PickFileButton.IsEnabled = on;
        BlankPanel.IsEnabled = on;
        FpsPanel.IsEnabled = on;
        LocateFfmpegButton.IsEnabled = on;
    }

    private void AppendOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        OutputLog.Text = OutputLog.Text is { Length: > 4000 } long_
            ? long_[^4000..] + Environment.NewLine + line
            : (OutputLog.Text ?? "") + Environment.NewLine + line;
        OutputScroll.ScrollToEnd();
    }

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard || CommandBox.Text is not { Length: > 0 } text) return;
        await clipboard.SetValueAsync(DataFormat.Text, text);
        CopyButton.Content = "Copied";
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
