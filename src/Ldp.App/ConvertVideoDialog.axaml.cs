using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ldp.Project;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ldp.App;

/// <summary>One source video queued for conversion.</summary>
public sealed class ConvertSourceItem(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Converts source videos (mkv/mp4/webm, …) into Hypseus-ready <c>.m2v</c> (+ <c>.ogg</c>)
/// with an external FFmpeg. Shows the exact command, runs it with live progress, and
/// remembers the ffmpeg.exe path. On close, <see cref="ProducedM2v"/> lists the .m2v files
/// created and <see cref="AddToProject"/> says whether the caller should add them.
/// </summary>
public partial class ConvertVideoDialog : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ConvertSourceItem> _sources = [];
    private string? _ffmpeg;
    private CancellationTokenSource? _cts;
    private bool _converting;

    /// <summary>Full paths of the .m2v files successfully created this session.</summary>
    public List<string> ProducedM2v { get; } = [];

    public bool AddToProject => AutoAddCheck.IsChecked == true;

    public ConvertVideoDialog()
    {
        InitializeComponent();
        _settings = new AppSettings();
        SourceList.ItemsSource = _sources;
    }

    public ConvertVideoDialog(AppSettings settings, bool projectOpen, IReadOnlyList<string>? seedFiles = null) : this()
    {
        _settings = settings;

        AutoAddCheck.IsChecked = projectOpen;
        AutoAddCheck.IsEnabled = projectOpen;

        // Prefer a remembered path; otherwise try to find one already on PATH.
        string? candidate = FfmpegTool.IsValidExe(settings.FfmpegPath)
            ? settings.FfmpegPath
            : FfmpegTool.ProbeSystem();
        if (FfmpegTool.IsValidExe(candidate))
            SetFfmpeg(candidate!, persist: !string.Equals(candidate, settings.FfmpegPath, StringComparison.OrdinalIgnoreCase));

        if (seedFiles != null)
            foreach (string f in seedFiles) AddSource(f);

        UpdateSourceUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    // ---------- FFmpeg location ----------

    private void SetFfmpeg(string path, bool persist)
    {
        _ffmpeg = path;
        FfmpegStatus.Foreground = (IBrush?)this.FindResource("FgMuted");
        FfmpegStatus.Text = "✓ " + path;
        if (persist)
        {
            _settings.FfmpegPath = path;
            _settings.Save();
        }
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
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private void OnOpenFfmpegDownload(object? sender, PointerPressedEventArgs e) => OpenUrl(FfmpegTool.DownloadUrl);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ---------- Source list ----------

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add source videos",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Video (mkv, mp4, webm)")
                    { Patterns = ["*.mkv", "*.mp4", "*.webm", "*.mov", "*.m4v", "*.avi", "*.ts"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        foreach (IStorageFile f in files)
            if (f.TryGetLocalPath() is { } p) AddSource(p);
        UpdateSourceUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add every convertible video in a folder",
        });
        string? dir = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (dir == null) return;
        try
        {
            foreach (string p in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (FfmpegCommand.IsConvertibleInput(p)) AddSource(p);
        }
        catch (Exception) { /* unreadable folder — nothing added */ }
        UpdateSourceUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private void AddSource(string path)
    {
        if (_sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        _sources.Add(new ConvertSourceItem(path));
    }

    private void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (SourceList.SelectedItem is ConvertSourceItem s) _sources.Remove(s);
        UpdateSourceUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private void OnClearSources(object? sender, RoutedEventArgs e)
    {
        _sources.Clear();
        UpdateSourceUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RemoveSourceButton.IsEnabled = !_converting && SourceList.SelectedItem != null;

    private void UpdateSourceUi()
    {
        SourceCount.Text = _sources.Count == 0 ? "" : $"{_sources.Count} file(s)";
        ClearSourcesButton.IsEnabled = !_converting && _sources.Count > 0;
        RemoveSourceButton.IsEnabled = !_converting && SourceList.SelectedItem != null;
    }

    // ---------- Options ----------

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        VideoBitrateBox.IsEnabled = VqCustom.IsChecked == true;
        AudioBitrateBox.IsEnabled = AudioCheck.IsChecked == true && AqCustom.IsChecked == true;
        UpdateCommandPreview();
    }

    private void OnOptionValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateCommandPreview();

    private void OnResizeToggled(object? sender, RoutedEventArgs e)
    {
        ResizePanel.IsVisible = ResizeCheck.IsChecked == true;
        UpdateCommandPreview();
    }

    private void OnAudioToggled(object? sender, RoutedEventArgs e)
    {
        AudioPanel.IsEnabled = AudioCheck.IsChecked == true;
        AudioBitrateBox.IsEnabled = AudioCheck.IsChecked == true && AqCustom.IsChecked == true;
        UpdateCommandPreview();
    }

    private void OnResPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        (int w, int h)? preset = ResPreset.SelectedIndex switch
        {
            0 => (854, 480),
            1 => (1280, 720),
            2 => (1920, 1080),
            _ => null,
        };
        if (preset is { } p) { WidthBox.Value = p.w; HeightBox.Value = p.h; }
        UpdateCommandPreview();
    }

    private ConvertOptions ReadOptions() => new()
    {
        Video = VqHighest.IsChecked == true ? VideoQuality.Highest
              : VqBalanced.IsChecked == true ? VideoQuality.Balanced
              : VideoQuality.Custom,
        CustomVideoBitrateK = (int)(VideoBitrateBox.Value ?? 6000),
        Resize = ResizeCheck.IsChecked == true,
        Width = (int)(WidthBox.Value ?? 1280),
        Height = (int)(HeightBox.Value ?? 720),
        CreateAudio = AudioCheck.IsChecked == true,
        Audio = AqStandard.IsChecked == true ? AudioQuality.Standard : AudioQuality.Custom,
        CustomAudioBitrateK = (int)(AudioBitrateBox.Value ?? 160),
        AudioOffsetMs = (int)(OffsetBox.Value ?? 110),
    };

    private void UpdateCommandPreview()
    {
        ConvertOptions o = ReadOptions();
        string exe = _ffmpeg ?? "ffmpeg";
        string sample = _sources.Count > 0 ? _sources[0].Path : @"C:\videos\input.mkv";
        FfmpegJob job = FfmpegCommand.Build(sample, o);

        string text = FfmpegCommand.Display(exe, job.VideoArgs);
        if (job.AudioArgs != null)
            text += Environment.NewLine + Environment.NewLine + FfmpegCommand.Display(exe, job.AudioArgs);
        CommandBox.Text = text;

        CommandNote.Text = _sources.Count > 1
            ? $"shown for {_sources[0].Name} — repeated for {_sources.Count} files"
            : "";
    }

    private void UpdateConvertEnabled() =>
        ConvertButton.IsEnabled = !_converting && _ffmpeg != null && _sources.Count > 0;

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clip) await clip.SetValueAsync(DataFormat.Text, CommandBox.Text ?? "");
    }

    // ---------- Convert ----------

    private async void OnConvert(object? sender, RoutedEventArgs e)
    {
        if (_converting || _ffmpeg == null || _sources.Count == 0) return;

        _converting = true;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        SetInputsEnabled(false);
        ConvertButton.Content = "Converting…";
        CloseButton.Content = "Cancel";
        ProgressArea.IsVisible = true;
        OutputLog.Text = "";
        ProgressDetail.Text = "";
        ConvProgress.Value = 0;

        ConvertOptions o = ReadOptions();
        var jobs = _sources.Select(s => FfmpegCommand.Build(s.Path, o)).ToList();

        int done = 0, failed = 0;
        for (int i = 0; i < jobs.Count && !ct.IsCancellationRequested; i++)
        {
            FfmpegJob job = jobs[i];
            string name = Path.GetFileName(job.InputPath);

            ProgressStatus.Text = $"File {i + 1}/{jobs.Count}: {name} — video…";
            ConvProgress.Value = 0;
            AppendOutput($"── {name}  →  {Path.GetFileName(job.M2vPath)} ──");

            FfmpegTool.RunResult vr = await RunOne(job.VideoArgs, ct);
            if (!vr.Ok)
            {
                failed++;
                AppendOutput(ct.IsCancellationRequested ? "cancelled." : $"video FAILED (exit {vr.ExitCode}). {vr.Tail}");
                continue;
            }
            if (!ProducedM2v.Contains(job.M2vPath)) ProducedM2v.Add(job.M2vPath);

            if (job.AudioArgs != null && !ct.IsCancellationRequested)
            {
                ProgressStatus.Text = $"File {i + 1}/{jobs.Count}: {name} — audio…";
                ConvProgress.Value = 0;
                FfmpegTool.RunResult ar = await RunOne(job.AudioArgs, ct);
                if (!ar.Ok && !ct.IsCancellationRequested)
                    AppendOutput($"⚠ audio failed (exit {ar.ExitCode}) — the .m2v is still fine. {ar.Tail}");
            }
            done++;
        }

        bool cancelled = ct.IsCancellationRequested;
        ProgressStatus.Text = cancelled
            ? $"Cancelled — {done} of {jobs.Count} completed."
            : $"Done — {done} of {jobs.Count} converted" + (failed > 0 ? $", {failed} failed." : ".");
        if (!cancelled) ConvProgress.Value = 1;

        _converting = false;
        _cts.Dispose();
        _cts = null;
        SetInputsEnabled(true);
        ConvertButton.Content = "▶ Convert";
        CloseButton.Content = ProducedM2v.Count > 0 && AddToProject ? "Add & Close" : "Close";
        UpdateConvertEnabled();
        UpdateSourceUi();
    }

    private Task<FfmpegTool.RunResult> RunOne(IReadOnlyList<string> args, CancellationToken ct) =>
        FfmpegTool.RunAsync(_ffmpeg!, args,
            line => Dispatcher.UIThread.Post(() => OnFfmpegLine(line)),
            frac => Dispatcher.UIThread.Post(() => ConvProgress.Value = frac),
            ct);

    private void OnFfmpegLine(string line)
    {
        // FFmpeg's repeating progress line goes to the single live-detail label;
        // everything else (stream info, warnings, errors) is kept in the log.
        string trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.Contains("frame=") || trimmed.Contains("time="))
            ProgressDetail.Text = trimmed;
        else
            AppendOutput(trimmed);
    }

    private void AppendOutput(string line)
    {
        string current = OutputLog.Text ?? "";
        current = current.Length == 0 ? line : current + "\n" + line;
        const int max = 8000;
        OutputLog.Text = current.Length > max ? current[^max..] : current;
        Dispatcher.UIThread.Post(OutputScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void SetInputsEnabled(bool on)
    {
        LocateFfmpegButton.IsEnabled = on;
        AddFilesButton.IsEnabled = on;
        AddFolderButton.IsEnabled = on;
        ClearSourcesButton.IsEnabled = on && _sources.Count > 0;
        RemoveSourceButton.IsEnabled = on && SourceList.SelectedItem != null;
        VqHighest.IsEnabled = VqBalanced.IsEnabled = VqCustom.IsEnabled = on;
        VideoBitrateBox.IsEnabled = on && VqCustom.IsChecked == true;
        ResizeCheck.IsEnabled = on;
        ResPreset.IsEnabled = WidthBox.IsEnabled = HeightBox.IsEnabled = on;
        AudioCheck.IsEnabled = on;
        AudioPanel.IsEnabled = on && AudioCheck.IsChecked == true;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_converting) { _cts?.Cancel(); return; }
        Close();
    }
}
