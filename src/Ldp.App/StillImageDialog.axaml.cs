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

/// <summary>One image queued for conversion, with its probed pixel size.</summary>
public sealed class StillSourceItem(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool ProbeStarted { get; set; }

    public bool HasSize => Width > 0 && Height > 0;
    public double Aspect => HasSize ? (double)Width / Height : 0;

    private string _info = "";
    public string Info
    {
        get => _info;
        set { if (_info != value) { _info = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Info))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetSize(MediaInfo? media)
    {
        if (media is { HasVideo: true })
        {
            Width = media.Width;
            Height = media.Height;
            Info = $"{Width}×{Height}";
        }
        else
        {
            Info = "couldn't read the image size";
        }
    }
}

/// <summary>
/// Generates a short .m2v passage from a still image, because Singe can only point
/// at frame numbers — it has no way to display an image file. Matches the picture
/// size and frame rate to the project's existing videos so the result drops
/// straight into the same global frame space.
/// </summary>
public partial class StillImageDialog : Window
{
    /// <summary>A framework slot this passage could fill. Exactly one of
    /// <see cref="Range"/> / <see cref="Still"/> is set; both null means "don't assign".</summary>
    public sealed record SlotChoice(string Display, RangeSlot? Range, StillSlot? Still)
    {
        public override string ToString() => Display;
    }

    /// <summary>One generated passage and what the caller should do with it.
    /// <paramref name="SlotFrameOffset"/> is the frame within the passage a
    /// single-frame slot should point at.</summary>
    public sealed record ProducedStill(
        string M2vPath,
        string SceneName,
        int SlotFrameOffset,
        RangeSlot? Range,
        StillSlot? Still);

    private readonly AppSettings _settings;
    private readonly ObservableCollection<StillSourceItem> _sources = [];
    private readonly (int Width, int Height, double Fps)? _matchTo;
    private readonly HashSet<string> _projectVideos;
    private readonly LdpProject? _project;
    private readonly List<SlotChoice> _slotChoices = [];

    private string _outputFolder = "";
    private string? _ffmpeg;
    private CancellationTokenSource? _cts;
    private bool _generating;
    private bool _updating;
    private bool _nameEditedByUser;

    /// <summary>False until every named control exists. Setting a control's value in
    /// XAML raises its changed event during parsing, before the generated fields are
    /// assigned — handlers must not run then.</summary>
    private bool _ready;

    /// <summary>The passages successfully generated this session, in order.</summary>
    public List<ProducedStill> Produced { get; } = [];

    public bool AddToProject => AutoAddCheck.IsChecked == true;

    public StillImageDialog()
    {
        InitializeComponent();
        _settings = new AppSettings();
        _projectVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ImageList.ItemsSource = _sources;
        _ready = true;
    }

    /// <param name="outputFolder">Where the .m2v files land — the game's Video folder.</param>
    /// <param name="matchTo">The project's picture size and rate; when set, the new
    /// passage is locked to it (a mismatched rate silently breaks every move's timing).</param>
    /// <param name="projectVideoPaths">Files already in the project's frame map, which
    /// must never be overwritten from here.</param>
    public StillImageDialog(AppSettings settings, string? outputFolder,
                            (int Width, int Height, double Fps)? matchTo,
                            IReadOnlyList<string>? projectVideoPaths,
                            LdpProject? project) : this()
    {
        _settings = settings;
        _matchTo = matchTo;
        _project = project;
        _projectVideos = new HashSet<string>(
            (projectVideoPaths ?? []).Select(SafeFullPath), StringComparer.OrdinalIgnoreCase);
        _outputFolder = outputFolder ?? "";

        bool projectOpen = project != null;
        AutoAddCheck.IsChecked = projectOpen;
        AutoAddCheck.IsEnabled = projectOpen;

        FitCombo.ItemsSource = new List<string>
        {
            "Stretch to fill",
            "Fit inside and pad with black",
        };
        FitCombo.SelectedIndex = 0;

        FpsCombo.ItemsSource = StillImage.Mpeg2Rates.ToList();
        FpsCombo.SelectedIndex = 0;

        // With videos in the project there is nothing to choose: the new passage
        // has to share their picture size and rate to sit in the same frame space.
        if (_matchTo is { } m)
        {
            PictureEditPanel.IsVisible = false;
            MatchedNote.IsVisible = true;
            StillImage.Mpeg2Rate rate = StillImage.NearestRate(m.Fps);
            MatchedNote.Text = $"Matched to the project's videos: {m.Width}×{m.Height} at {rate.Fps:0.###} fps " +
                               $"({rate.Rational}).";
            if (!StillImage.IsLegalRate(m.Fps))
            {
                MatchedNote.Foreground = (IBrush?)this.FindResource("AccentAmber");
                MatchedNote.Text += $" The project's videos report {m.Fps:0.###} fps, which is not one of MPEG-2's " +
                                    "eight legal rates — the closest is used here.";
            }
        }

        BuildSlotChoices();

        string? candidate = FfmpegTool.IsValidExe(settings.FfmpegPath)
            ? settings.FfmpegPath
            : FfmpegTool.ProbeSystem();
        if (FfmpegTool.IsValidExe(candidate))
            SetFfmpeg(candidate!, persist: !string.Equals(candidate, settings.FfmpegPath, StringComparison.OrdinalIgnoreCase));

        RefreshOutputUi();
        UpdateLengthUi();
        UpdateCommandPreview();
        UpdateGenerateEnabled();
    }

    private StillSourceItem? Sel => ImageList.SelectedItem as StillSourceItem;

    private static string SafeFullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    // ---------- Slots ----------

    /// <summary>Offers every framework slot an image could fill, stills first —
    /// a still frame is what artwork is normally for. Slots already filled are
    /// marked so an author does not overwrite one by accident.</summary>
    private void BuildSlotChoices()
    {
        _slotChoices.Clear();
        _slotChoices.Add(new SlotChoice("— don't assign it, just add the video —", null, null));

        if (_project != null)
        {
            foreach (SlotCatalog.StillInfo s in SlotCatalog.Stills)
            {
                string filled = _project.Slots.Stills.TryGetValue(s.Slot, out int f) && f != 0
                    ? $"  (currently frame {f})" : "";
                string req = s.Required ? " *" : "";
                _slotChoices.Add(new SlotChoice($"Still: {s.Display}{req}  ·  {s.LuaName}{filled}", null, s.Slot));
            }
            foreach (SlotCatalog.RangeInfo r in SlotCatalog.Ranges)
            {
                string filled = _project.Slots.Ranges.ContainsKey(r.Slot) ? "  (already set)" : "";
                string req = r.Required ? " *" : "";
                _slotChoices.Add(new SlotChoice($"Video: {r.Display}{req}  ·  {r.LuaName}{filled}", r.Slot, null));
            }
        }

        SlotCombo.ItemsSource = _slotChoices;
        SlotCombo.SelectedIndex = 0;
        SlotPanel.IsVisible = _project != null;
        UpdateSlotHint();
    }

    private SlotChoice CurrentSlot =>
        SlotCombo.SelectedItem as SlotChoice ?? _slotChoices.FirstOrDefault()
        ?? new SlotChoice("", null, null);

    private void OnSlotChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateSlotHint();
    }

    private void UpdateSlotHint()
    {
        if (_project == null) return;
        SlotChoice choice = CurrentSlot;
        bool single = _sources.Count <= 1;

        if (choice.Still is { } st)
        {
            SlotCatalog.StillInfo info = SlotCatalog.Stills.First(s => s.Slot == st);
            SlotHint.Text = $"{info.Hint} The slot is set to the middle frame of the new passage, which is " +
                            "always at full brightness whatever the fades do.";
        }
        else if (choice.Range is { } rg)
        {
            SlotCatalog.RangeInfo info = SlotCatalog.Ranges.First(r => r.Slot == rg);
            SlotHint.Text = $"{info.Hint} A scene covering the whole passage is created and assigned to the slot.";
        }
        else
        {
            SlotHint.Text = "The passage is added as a project video with a scene covering it, ready to " +
                            "assign in Game Setup whenever you like.";
        }

        SlotCombo.IsEnabled = !_generating && single;
        if (!single) SlotHint.Text = "Slot assignment is for one image at a time — the queue holds " +
                                     $"{_sources.Count}. Each passage is still added with its own scene.";
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
        foreach (StillSourceItem item in _sources) ProbeItem(item);
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
        UpdateGenerateEnabled();
    }

    private void OnOpenFfmpegDownload(object? sender, PointerPressedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = FfmpegTool.DownloadUrl, UseShellExecute = true }); }
        catch (Exception) { }
    }

    // ---------- Source images ----------

    private async void OnAddImages(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add still images",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images (png, jpg, bmp, webp)")
                    { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff", "*.tga", "*.gif"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        foreach (IStorageFile f in files)
            if (f.TryGetLocalPath() is { } p) AddSource(p);
        AfterSourcesChanged();
    }

    private void AddSource(string path)
    {
        if (_sources.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = new StillSourceItem(path)
        {
            Info = _ffmpeg == null ? "locate ffmpeg to read the image size" : "reading…",
        };
        _sources.Add(item);
        if (ImageList.SelectedIndex < 0) ImageList.SelectedIndex = 0;
        ProbeItem(item);
    }

    private async void ProbeItem(StillSourceItem item)
    {
        if (_ffmpeg == null || item.ProbeStarted) return;
        item.ProbeStarted = true;
        item.Info = "reading…";
        MediaInfo? media = await FfmpegTool.ProbeAsync(_ffmpeg, item.Path);
        item.SetSize(media);

        // Without a project to match, the first image's own size is the sanest default.
        if (_matchTo == null && _sources.Count == 1 && item.HasSize && !_updating)
        {
            _updating = true;
            WidthBox.Value = item.Width - item.Width % 2;
            HeightBox.Value = item.Height - item.Height % 2;
            _updating = false;
        }
        UpdateAspectWarning();
        UpdateCommandPreview();
    }

    private void AfterSourcesChanged()
    {
        UpdateSourceUi();
        RefreshOutputUi();
        UpdateSlotHint();
        UpdateAspectWarning();
        UpdateCommandPreview();
        UpdateGenerateEnabled();
    }

    private void OnRemoveImage(object? sender, RoutedEventArgs e)
    {
        if (Sel is { } s) _sources.Remove(s);
        AfterSourcesChanged();
    }

    private void OnClearImages(object? sender, RoutedEventArgs e)
    {
        _sources.Clear();
        AfterSourcesChanged();
    }

    private void OnImageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RemoveImageButton.IsEnabled = !_generating && Sel != null;
        RefreshOutputUi();
        UpdateAspectWarning();
        UpdateCommandPreview();
    }

    private void UpdateSourceUi()
    {
        ImageCount.Text = _sources.Count == 0 ? "" : $"{_sources.Count} image(s)";
        MultiNote.IsVisible = _sources.Count > 1;
        ClearImagesButton.IsEnabled = !_generating && _sources.Count > 0;
        RemoveImageButton.IsEnabled = !_generating && Sel != null;
    }

    // ---------- Picture ----------

    private (int Width, int Height) TargetSize() =>
        _matchTo is { } m ? (m.Width, m.Height) : ((int)(WidthBox.Value ?? 1600), (int)(HeightBox.Value ?? 900));

    private double TargetFps() =>
        _matchTo is { } m ? m.Fps
        : (FpsCombo.SelectedItem as StillImage.Mpeg2Rate ?? StillImage.Mpeg2Rates[0]).Fps;

    private void OnFpsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        UpdateLengthUi();
        UpdateCommandPreview();
    }

    private void OnFitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        UpdateAspectWarning();
        UpdateCommandPreview();
    }

    /// <summary>Warns when the artwork's shape differs from the target picture, which
    /// stretching would visibly distort — an easy thing to miss on a title card.</summary>
    private void UpdateAspectWarning()
    {
        StillSourceItem? item = Sel ?? _sources.FirstOrDefault();
        (int w, int h) = TargetSize();
        if (item is not { HasSize: true } || h <= 0)
        {
            AspectWarning.IsVisible = false;
            return;
        }

        double target = (double)w / h;
        bool differs = Math.Abs(item.Aspect - target) / target > 0.01;
        bool padding = FitCombo.SelectedIndex == 1;

        AspectWarning.IsVisible = differs;
        AspectWarning.Text = padding
            ? $"{item.Name} is {item.Width}×{item.Height} — a different shape from {w}×{h}, so it is scaled " +
              "down to fit and centred on black bars."
            : $"{item.Name} is {item.Width}×{item.Height} — a different shape from {w}×{h}, so stretching " +
              "will distort it. Switch to \"Fit inside and pad with black\" to keep its proportions.";
        AspectWarning.Foreground = (IBrush?)this.FindResource(padding ? "FgMuted" : "AccentAmber");
    }

    // ---------- Length ----------

    private void OnFadeToggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        FadePanel.IsVisible = FadeHint.IsVisible = FadeCheck.IsChecked == true;
        UpdateLengthUi();
        UpdateCommandPreview();
    }

    private void OnNumberChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        UpdateLengthUi();
        UpdateAspectWarning();
        UpdateCommandPreview();
    }

    private void UpdateLengthUi()
    {
        StillOptions o = ReadOptions();
        int frames = StillImage.FrameCount(o.Seconds, o.Fps);
        StillImage.Mpeg2Rate rate = StillImage.NearestRate(o.Fps);
        FrameCountText.Text = $"= {frames} frames @ {rate.Fps:0.###} fps";

        IReadOnlyList<string> problems = StillImage.Validate(o);
        ProblemsText.IsVisible = problems.Count > 0;
        ProblemsText.Text = string.Join("  ", problems);
        UpdateGenerateEnabled();
    }

    private StillOptions ReadOptions()
    {
        (int w, int h) = TargetSize();
        return new StillOptions
        {
            Seconds = (double)(LengthBox.Value ?? 5m),
            Width = w,
            Height = h,
            Fps = TargetFps(),
            Fade = FadeCheck.IsChecked == true,
            FadeInSeconds = (double)(FadeInBox.Value ?? 0.5m),
            FadeOutSeconds = (double)(FadeOutBox.Value ?? 0.5m),
            Fit = FitCombo.SelectedIndex == 1 ? StillFit.Pad : StillFit.Stretch,
        };
    }

    // ---------- Output ----------

    private void OnOutNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating || !_ready) return;
        _nameEditedByUser = true;
        RefreshOutputUi();
        UpdateCommandPreview();
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should the .m2v files be written?",
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } dir)
        {
            _outputFolder = dir;
            RefreshOutputUi();
            UpdateCommandPreview();
        }
    }

    /// <summary>The output name for one image: the typed name when a single image is
    /// queued, otherwise the image's own base name.</summary>
    private string OutputNameFor(StillSourceItem item)
    {
        if (_sources.Count == 1)
        {
            string typed = (OutNameBox.Text ?? "").Trim();
            if (typed.Length > 0)
                return typed.EndsWith(".m2v", StringComparison.OrdinalIgnoreCase) ? typed : typed + ".m2v";
            return "output.m2v";
        }
        return StillImage.SuggestOutputName(item.Path);
    }

    private string OutputPathFor(StillSourceItem item) =>
        System.IO.Path.Combine(_outputFolder.Length > 0 ? _outputFolder : ".", OutputNameFor(item));

    private void RefreshOutputUi()
    {
        _updating = true;
        try
        {
            OutFolderText.Text = _outputFolder.Length > 0 ? _outputFolder : "(pick a folder)";

            bool single = _sources.Count == 1;
            OutNameBox.IsEnabled = single && !_generating;

            // Seed the name from the image so outputs line up with the artwork;
            // once the author types their own, leave it alone.
            if (single && !_nameEditedByUser)
                OutNameBox.Text = StillImage.SuggestOutputName(_sources[0].Path);
            else if (!single)
                OutNameBox.Text = _sources.Count == 0 ? "" : "(named after each image)";
        }
        finally
        {
            _updating = false;
        }

        UpdateOutputWarning();
    }

    /// <summary>Flags an output that would replace something — and refuses outright to
    /// overwrite a video the project's frame map depends on.</summary>
    private bool UpdateOutputWarning()
    {
        var clashes = new List<string>();
        var existing = new List<string>();
        bool spaces = false;

        foreach (StillSourceItem item in _sources)
        {
            string path = OutputPathFor(item);
            string name = System.IO.Path.GetFileName(path);
            if (name.Contains(' ')) spaces = true;
            if (_projectVideos.Contains(SafeFullPath(path))) clashes.Add(name);
            else if (File.Exists(path)) existing.Add(name);
        }

        if (clashes.Count > 0)
        {
            OutNameNote.IsVisible = true;
            OutNameNote.Foreground = (IBrush?)this.FindResource("PortDeath");
            OutNameNote.Text = $"{string.Join(", ", clashes)} is already a video in this project. Overwriting it " +
                               "would change its frame count and shift every scene after it — pick another name.";
            return false;
        }

        var notes = new List<string>();
        if (existing.Count > 0)
            notes.Add($"{string.Join(", ", existing)} already exists here and will be replaced.");
        if (spaces)
            notes.Add("A name with spaces can trip up the frame file — underscores are safer.");

        OutNameNote.IsVisible = notes.Count > 0;
        OutNameNote.Foreground = (IBrush?)this.FindResource("AccentAmber");
        OutNameNote.Text = string.Join("  ", notes);
        return true;
    }

    // ---------- Command preview ----------

    private void UpdateCommandPreview()
    {
        StillOptions o = ReadOptions();
        string exe = _ffmpeg ?? "ffmpeg";
        StillSourceItem? item = Sel ?? _sources.FirstOrDefault();

        string image = item?.Path ?? @"C:\artwork\instructions.png";
        string output = item != null ? OutputPathFor(item)
            : System.IO.Path.Combine(_outputFolder.Length > 0 ? _outputFolder : ".", "output.m2v");

        CommandBox.Text = FfmpegCommand.Display(exe, StillImage.BuildArgs(image, output, o));
        CommandNote.Text = item != null && _sources.Count > 1
            ? $"shown for {item.Name} — each image runs the same way"
            : "";
    }

    private void UpdateGenerateEnabled()
    {
        bool ok = !_generating && _ffmpeg != null && _sources.Count > 0 &&
                  _outputFolder.Length > 0 && StillImage.Validate(ReadOptions()).Count == 0;
        GenerateButton.IsEnabled = ok;
    }

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clip) await clip.SetValueAsync(DataFormat.Text, CommandBox.Text ?? "");
    }

    private void OnAutoAddToggled(object? sender, RoutedEventArgs e)
    {
        if (_ready) UpdateSlotHint();
    }

    // ---------- Generate ----------

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_generating || _ffmpeg == null || _sources.Count == 0) return;
        if (!UpdateOutputWarning()) return;

        StillOptions o = ReadOptions();
        if (StillImage.Validate(o).Count > 0) return;

        _generating = true;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        SetInputsEnabled(false);
        GenerateButton.Content = "Generating…";
        CloseButton.Content = "Cancel";
        ProgressArea.IsVisible = true;
        OutputLog.Text = "";
        ProgressDetail.Text = "";
        StillProgress.IsIndeterminate = true;

        SlotChoice slot = CurrentSlot;
        List<StillSourceItem> sources = _sources.ToList();
        int done = 0, failed = 0;

        for (int i = 0; i < sources.Count && !ct.IsCancellationRequested; i++)
        {
            StillSourceItem item = sources[i];
            string output = OutputPathFor(item);

            ProgressStatus.Text = $"Image {i + 1}/{sources.Count}: {item.Name} → {System.IO.Path.GetFileName(output)}";
            AppendOutput($"── {item.Name}  →  {System.IO.Path.GetFileName(output)} ──");

            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!); }
            catch (Exception ex) { AppendOutput("couldn't create the output folder: " + ex.Message); failed++; continue; }

            FfmpegTool.RunResult r = await FfmpegTool.RunAsync(
                _ffmpeg, StillImage.BuildArgs(item.Path, output, o),
                line => Dispatcher.UIThread.Post(() => OnFfmpegLine(line)),
                null, ct);

            if (!r.Ok)
            {
                failed++;
                AppendOutput(ct.IsCancellationRequested ? "cancelled." : $"FAILED (exit {r.ExitCode}). {r.Tail}");
                continue;
            }

            // Slot assignment is a single-image action; a queue just adds videos.
            bool assign = sources.Count == 1;
            Produced.Add(new ProducedStill(
                output,
                System.IO.Path.GetFileNameWithoutExtension(output),
                o.MidFrame,
                assign ? slot.Range : null,
                assign ? slot.Still : null));
            done++;
        }

        bool cancelled = ct.IsCancellationRequested;
        StillProgress.IsIndeterminate = false;
        StillProgress.Value = cancelled ? 0 : 1;
        ProgressStatus.Text = cancelled
            ? $"Cancelled — {done} of {sources.Count} generated."
            : $"Done — {done} of {sources.Count} generated" + (failed > 0 ? $", {failed} failed." : ".");

        _generating = false;
        _cts.Dispose();
        _cts = null;
        SetInputsEnabled(true);
        GenerateButton.Content = "▶ Generate";
        CloseButton.Content = Produced.Count > 0 && AddToProject ? "Add & Close" : "Close";
        UpdateSourceUi();
        UpdateGenerateEnabled();
    }

    private void OnFfmpegLine(string line)
    {
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
        AddImagesButton.IsEnabled = on;
        ClearImagesButton.IsEnabled = on && _sources.Count > 0;
        RemoveImageButton.IsEnabled = on && Sel != null;
        PictureEditPanel.IsEnabled = on;
        FitCombo.IsEnabled = on;
        LengthBox.IsEnabled = on;
        FadeCheck.IsEnabled = on;
        FadePanel.IsEnabled = on;
        OutNameBox.IsEnabled = on && _sources.Count == 1;
        BrowseFolderButton.IsEnabled = on;
        SlotCombo.IsEnabled = on && _sources.Count <= 1;
        AutoAddCheck.IsEnabled = on && _project != null;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_generating) { _cts?.Cancel(); return; }
        Close();
    }
}
