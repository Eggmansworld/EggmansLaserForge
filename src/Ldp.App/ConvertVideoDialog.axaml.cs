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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ldp.App;

/// <summary>One source video queued for conversion, with its probed stream info and
/// its own per-file choices (which audio track to convert, downscale or not).</summary>
public sealed class ConvertSourceItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Stream layout read via <c>ffmpeg -i</c>; null until probed (or unreadable).</summary>
    public MediaInfo? Media { get; private set; }
    public bool ProbeStarted { get; set; }
    public bool Probed { get; private set; }

    /// <summary>Audio stream to convert (<c>-map 0:a:{n}</c>).</summary>
    public int AudioTrack { get; set; }

    /// <summary>Output size; null = keep the source resolution.</summary>
    public (int Width, int Height)? Scale { get; set; }

    /// <summary>True when the user picked "Custom…" rather than a preset.</summary>
    public bool CustomScale { get; set; }

    private string _info = "";
    public string Info
    {
        get => _info;
        set { if (_info != value) { _info = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Info))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetMedia(MediaInfo? media)
    {
        Media = media;
        Probed = true;
        if (media == null)
        {
            Info = "couldn't read streams — the first audio track will be used";
            return;
        }
        AudioTrack = Math.Max(0, media.DefaultAudioTrack);

        // A >1080p master defaults to the largest legal downscale (usually
        // 1920×1080): Hypseus is not designed for video above 1080p.
        if (media.ExceedsHypseusLimit && Scale == null && !CustomScale &&
            MediaInfo.DownscalePresets(media.Width, media.Height) is { Count: > 0 } presets)
            Scale = presets[0];

        string fps = media.Fps > 0 ? $" · {media.Fps:0.##} fps" : "";
        string tracks = $" · {media.AudioTracks.Count} audio track{(media.AudioTracks.Count == 1 ? "" : "s")}";
        string warn = media.ExceedsHypseusLimit ? " · above 1080p — downscale recommended" : "";
        Info = $"{media.Width}×{media.Height}{fps}{tracks}{warn}";
    }
}

/// <summary>
/// Converts source videos (mkv/mp4/webm, …) into Hypseus-ready <c>.m2v</c> (+ <c>.ogg</c>)
/// with an external FFmpeg. Each file is probed so the user can pick the audio track
/// (language/format) and a downscale for >1080p masters. Shows the exact command,
/// runs it with live progress, and remembers the ffmpeg.exe path. On close,
/// <see cref="ProducedM2v"/> lists the .m2v files created and <see cref="AddToProject"/>
/// says whether the caller should add them.
/// </summary>
public partial class ConvertVideoDialog : Window
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ConvertSourceItem> _sources = [];
    private string? _ffmpeg;
    private CancellationTokenSource? _cts;
    private bool _converting;
    private bool _updatingPerFile;

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
        RefreshPerFileUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private ConvertSourceItem? Sel => SourceList.SelectedItem as ConvertSourceItem;

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
        foreach (ConvertSourceItem item in _sources) ProbeItem(item);
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

    // ---------- Source list + probing ----------

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
        AfterSourcesChanged();
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
        AfterSourcesChanged();
    }

    private void AddSource(string path)
    {
        if (_sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = new ConvertSourceItem(path)
        {
            Info = _ffmpeg == null ? "locate ffmpeg to read streams" : "reading streams…",
        };
        _sources.Add(item);
        if (SourceList.SelectedIndex < 0) SourceList.SelectedIndex = 0;
        ProbeItem(item);
    }

    /// <summary>Reads the file's stream layout in the background; refreshes the
    /// per-file pickers when it lands on the currently highlighted file.</summary>
    private async void ProbeItem(ConvertSourceItem item)
    {
        if (_ffmpeg == null || item.ProbeStarted) return;
        item.ProbeStarted = true;
        item.Info = "reading streams…";
        MediaInfo? media = await FfmpegTool.ProbeAsync(_ffmpeg, item.Path);
        item.SetMedia(media);
        if (Sel == item) RefreshPerFileUi();
        UpdateCommandPreview();
    }

    private void AfterSourcesChanged()
    {
        UpdateSourceUi();
        RefreshPerFileUi();
        UpdateCommandPreview();
        UpdateConvertEnabled();
    }

    private void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (Sel is { } s) _sources.Remove(s);
        AfterSourcesChanged();
    }

    private void OnClearSources(object? sender, RoutedEventArgs e)
    {
        _sources.Clear();
        AfterSourcesChanged();
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RemoveSourceButton.IsEnabled = !_converting && Sel != null;
        RefreshPerFileUi();
        UpdateCommandPreview();
    }

    private void UpdateSourceUi()
    {
        SourceCount.Text = _sources.Count == 0 ? "" : $"{_sources.Count} file(s)";
        PerFileNote.IsVisible = _sources.Count > 1;
        ClearSourcesButton.IsEnabled = !_converting && _sources.Count > 0;
        RemoveSourceButton.IsEnabled = !_converting && Sel != null;
    }

    // ---------- Per-file pickers (audio track + resolution) ----------

    private void RefreshPerFileUi()
    {
        _updatingPerFile = true;
        try
        {
            ConvertSourceItem? item = Sel ?? _sources.FirstOrDefault();
            string tag = item != null && _sources.Count > 1 ? "for: " + item.Name : "";
            ResFileTag.Text = tag;
            TrackFileTag.Text = tag;

            // Audio track combo.
            if (item?.Media is { AudioTracks.Count: > 0 } media)
            {
                TrackCombo.ItemsSource = media.AudioTracks.Select(t => t.Display()).ToList();
                TrackCombo.SelectedIndex = Math.Clamp(item.AudioTrack, 0, media.AudioTracks.Count - 1);
                TrackCombo.IsEnabled = !_converting;
                TrackHint.Text = media.AudioTracks.Count == 1
                    ? "1 audio track found."
                    : $"{media.AudioTracks.Count} audio tracks found — pick the language and format that fit the game.";
            }
            else
            {
                TrackCombo.ItemsSource = null;
                TrackCombo.IsEnabled = false;
                TrackHint.Text = item == null ? "Locate ffmpeg and add a file to list its audio tracks."
                    : !item.Probed ? (_ffmpeg == null ? "Locate ffmpeg above to read this file's audio tracks." : "Reading streams…")
                    : item.Media == null ? "Couldn't read this file's streams — the first audio track will be used."
                    : "This file has no audio streams.";
            }

            // Resolution combo: Keep original / aspect-kept presets / Custom.
            var choices = new List<string>();
            IReadOnlyList<(int Width, int Height)> presets = [];
            if (item?.Media is { HasVideo: true } m)
            {
                choices.Add($"Keep original ({m.Width}×{m.Height})");
                presets = MediaInfo.DownscalePresets(m.Width, m.Height);
                choices.AddRange(presets.Select(p => $"{p.Width} × {p.Height}"));
            }
            else
            {
                choices.Add("Keep original");
            }
            choices.Add("Custom…");
            ResCombo.ItemsSource = choices;
            ResCombo.IsEnabled = item != null && !_converting;

            int selected = 0;
            if (item != null)
            {
                if (item.CustomScale) selected = choices.Count - 1;
                else if (item.Scale is { } sc)
                {
                    int at = -1;
                    for (int i = 0; i < presets.Count; i++)
                        if (presets[i] == sc) { at = i; break; }
                    if (at >= 0) selected = 1 + at;
                    else { selected = choices.Count - 1; item.CustomScale = true; }
                }
            }
            ResCombo.SelectedIndex = selected;
            ResCustomPanel.IsVisible = item != null && item.CustomScale;
            if (item is { CustomScale: true, Scale: { } custom })
            {
                WidthBox.Value = custom.Width;
                HeightBox.Value = custom.Height;
            }

            if (item?.Media is { ExceedsHypseusLimit: true } big)
            {
                ResWarning.Text = $"This source is {big.Width}×{big.Height} — Hypseus Singe is not designed for video " +
                                  "above 1920×1080 (playback gets too CPU-heavy). A 1080p downscale is pre-selected; " +
                                  "pick a smaller size if the game targets a low-power device.";
                ResWarning.IsVisible = true;
            }
            else
            {
                ResWarning.IsVisible = false;
            }
        }
        finally
        {
            _updatingPerFile = false;
        }
    }

    private void OnTrackChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingPerFile) return;
        if (Sel is { } item && TrackCombo.SelectedIndex >= 0)
            item.AudioTrack = TrackCombo.SelectedIndex;
        UpdateCommandPreview();
    }

    private void OnResChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingPerFile || Sel is not { } item) return;
        int index = ResCombo.SelectedIndex;
        int count = (ResCombo.ItemsSource as List<string>)?.Count ?? 0;
        if (index < 0 || count == 0) return;

        if (index == count - 1) // Custom…
        {
            item.CustomScale = true;
            item.Scale = ((int)(WidthBox.Value ?? 1280), (int)(HeightBox.Value ?? 720));
            ResCustomPanel.IsVisible = true;
        }
        else if (index == 0) // Keep original
        {
            item.CustomScale = false;
            item.Scale = null;
            ResCustomPanel.IsVisible = false;
        }
        else // preset
        {
            IReadOnlyList<(int, int)> presets = item.Media is { HasVideo: true } m
                ? MediaInfo.DownscalePresets(m.Width, m.Height) : [];
            if (index - 1 < presets.Count)
            {
                item.CustomScale = false;
                item.Scale = presets[index - 1];
            }
            ResCustomPanel.IsVisible = false;
        }
        UpdateCommandPreview();
    }

    // ---------- Options ----------

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        VideoBitrateBox.IsEnabled = VqCustom.IsChecked == true;
        AudioBitrateBox.IsEnabled = AudioCheck.IsChecked == true && AqCustom.IsChecked == true;
        UpdateCommandPreview();
    }

    private void OnOptionValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_updatingPerFile && Sel is { CustomScale: true } item &&
            (ReferenceEquals(sender, WidthBox) || ReferenceEquals(sender, HeightBox)))
            item.Scale = ((int)(WidthBox.Value ?? 1280), (int)(HeightBox.Value ?? 720));
        UpdateCommandPreview();
    }

    private void OnAudioToggled(object? sender, RoutedEventArgs e)
    {
        AudioPanel.IsEnabled = AudioCheck.IsChecked == true;
        AudioBitrateBox.IsEnabled = AudioCheck.IsChecked == true && AqCustom.IsChecked == true;
        UpdateCommandPreview();
    }

    private ConvertOptions ReadOptions() => new()
    {
        Video = VqHighest.IsChecked == true ? VideoQuality.Highest
              : VqBalanced.IsChecked == true ? VideoQuality.Balanced
              : VideoQuality.Custom,
        CustomVideoBitrateK = (int)(VideoBitrateBox.Value ?? 6000),
        CreateAudio = AudioCheck.IsChecked == true,
        Audio = AqStandard.IsChecked == true ? AudioQuality.Standard : AudioQuality.Custom,
        CustomAudioBitrateK = (int)(AudioBitrateBox.Value ?? 160),
        DownmixStereo = DownmixCheck.IsChecked == true,
        AudioOffsetMs = (int)(OffsetBox.Value ?? 110),
    };

    private void UpdateCommandPreview()
    {
        ConvertOptions o = ReadOptions();
        string exe = _ffmpeg ?? "ffmpeg";
        ConvertSourceItem? item = Sel ?? _sources.FirstOrDefault();
        string sample = item?.Path ?? @"C:\videos\input.mkv";
        FfmpegJob job = FfmpegCommand.Build(sample, o, null, item?.AudioTrack ?? 0, item?.Scale);

        string text = FfmpegCommand.Display(exe, job.VideoArgs);
        if (job.AudioArgs != null)
            text += Environment.NewLine + Environment.NewLine + FfmpegCommand.Display(exe, job.AudioArgs);
        CommandBox.Text = text;

        CommandNote.Text = item != null && _sources.Count > 1
            ? $"shown for {item.Name} — each file converts with its own track & resolution"
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
        var jobs = _sources.Select(s => FfmpegCommand.Build(s.Path, o, null, s.AudioTrack, s.Scale)).ToList();

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
        RefreshPerFileUi();
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
        RemoveSourceButton.IsEnabled = on && Sel != null;
        VqHighest.IsEnabled = VqBalanced.IsEnabled = VqCustom.IsEnabled = on;
        VideoBitrateBox.IsEnabled = on && VqCustom.IsChecked == true;
        TrackCombo.IsEnabled = on && Sel?.Media is { AudioTracks.Count: > 0 };
        ResCombo.IsEnabled = on && Sel != null;
        WidthBox.IsEnabled = HeightBox.IsEnabled = on;
        AudioCheck.IsEnabled = on;
        AudioPanel.IsEnabled = on && AudioCheck.IsChecked == true;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_converting) { _cts?.Cancel(); return; }
        Close();
    }
}
