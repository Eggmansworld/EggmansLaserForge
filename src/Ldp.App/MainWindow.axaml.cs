using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ldp.Engine;
using Ldp.Project;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ldp.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings = AppSettings.Load();
    private LdpProject? _project;
    private string? _projectPath;
    private bool _dirty;

    private readonly Dictionary<int, FrameEngine> _engines = [];
    private readonly Dictionary<int, AudioPlayer?> _audioPlayers = [];
    private readonly HashSet<int> _warnedNoAudio = [];
    private AudioPlayer? _playingAudio;
    private FrameEngine? _engine;
    private int _activeVideo = -1;

    private readonly ObservableCollection<VideoItem> _videoItems = [];
    private readonly ObservableCollection<ClipItem> _clipItems = [];
    private readonly ObservableCollection<InteractionItem> _interactionItems = [];
    private readonly ObservableCollection<string> _logItems = [];
    private string? _lastLogged;

    private WriteableBitmap? _bitmap;
    private DispatcherTimer? _playTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private bool _playing;
    private int _currentLocal;
    private int _counterDigits = 6;
    private bool _updatingSlider;
    private bool _suppressVideoSelection;
    private int? _markIn;         // global frame numbers, valid for _activeVideo
    private int? _markOut;
    private int? _playStopGlobal; // stop playback when this global frame is shown

    // Chained (storyboard flow) playback.
    private List<Clip>? _playQueue;
    private int _playQueueIndex;
    private Clip? _playingClip;        // scene currently playing (drives the moves panel)
    private bool _playIsSingleScene;   // Play Scene vs Play Flow, for the end banner
    private Guid? _scrolledActiveMove;  // last move auto-scrolled into view

    // Unlimited undo/redo: whole-project JSON snapshots. _lastSnapshot always
    // mirrors the current persisted state; every mutation pushes the previous
    // one onto the undo stack (see MarkDirty).
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private string? _lastSnapshot;

    private readonly SemaphoreSlim _decodeGate = new(1, 1);
    private long _requestSeq;

    public MainWindow()
    {
        InitializeComponent();
        Sdcb.FFmpeg.Utils.FFmpegLogger.LogLevel = Sdcb.FFmpeg.Raw.LogLevel.Error;
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        VideoImage.PointerWheelChanged += OnWheel;
        VideoList.ItemsSource = _videoItems;
        ClipList.ItemsSource = _clipItems;
        LogList.ItemsSource = _logItems;

        // Every status message is captured into the slide-open log history.
        StatusText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) AppendLog(StatusText.Text);
        };
        InteractionList.ItemsSource = _interactionItems;
        MarkerStrip.SizeChanged += (_, _) => RepaintMarkerStrip();

        // Scenes can be dragged from the bin onto the storyboard canvas.
        ClipList.AddHandler(PointerPressedEvent, OnClipListPointerPressed, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerMovedEvent, OnClipListPointerMoved, RoutingStrategies.Tunnel);
        ClipList.AddHandler(PointerReleasedEvent, (_, _) => { _binDragOrigin = null; _binDragPressArgs = null; }, RoutingStrategies.Tunnel);

        GameSetup.SlotsChanged += () => { MarkDirty(); SaveProject(); };
        GameSetup.GotoFrameRequested += async frame =>
        {
            ShowEditorPane();
            await JumpToGlobalAsync(frame);
        };

        Storyboard.GraphChanged += () => { MarkDirty(); SaveProject(); };
        Storyboard.NodeActivated += async clip =>
        {
            ShowEditorPane();
            await JumpToGlobalAsync(clip.StartFrame);
        };
        Storyboard.PlaySceneRequested += clip =>
        {
            ShowEditorPane();
            PlayClipSequence([clip]);
        };
        Storyboard.PlayFlowRequested += clips =>
        {
            ShowEditorPane();
            PlayClipSequence(clips);
        };
        ShowEditorPane();

        _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += (_, _) => { if (_dirty) SaveProject(auto: true); };
        _autosaveTimer.Start();

        Closing += (_, _) => { if (_dirty && _project != null) SaveProject(auto: true); };
        Opened += async (_, _) =>
        {
            if (_settings.LastProjectPath is { } last && File.Exists(last))
                await OpenProjectAsync(last);
        };
    }

    private int CurrentGlobal =>
        _project != null && _activeVideo >= 0 ? _project.ToGlobal(_activeVideo, _currentLocal) : _currentLocal;

    // ---------- Status log ----------

    private void AppendLog(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == _lastLogged) return;
        _lastLogged = text;
        _logItems.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (_logItems.Count > 300) _logItems.RemoveAt(0);
        if (LogPanel.IsVisible) ScrollLogToEnd();
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private void OnToggleLog(object? sender, RoutedEventArgs e)
    {
        LogPanel.IsVisible = !LogPanel.IsVisible;
        LogToggle.Content = LogPanel.IsVisible ? "Log ▼" : "Log ▲";
        if (LogPanel.IsVisible) ScrollLogToEnd();
    }

    private void ScrollLogToEnd() => Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd());

    private ClipItem? LookupClip(Guid id) => _clipItems.FirstOrDefault(c => c.Clip.Id == id);

    private Clip? SelectedClip => (ClipList.SelectedItem as ClipItem)?.Clip;

    /// <summary>True when the scene's frames live in the currently active video.</summary>
    private bool SceneIsActive(Clip clip) =>
        _project != null && _project.VideoIndexOf(clip.StartFrame) == _activeVideo;

    // ---------- Go to frame ----------

    private void OnGotoKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitGoto();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            GotoBox.Text = "";
            Focus();
            e.Handled = true;
        }
    }

    private void OnGotoClicked(object? sender, RoutedEventArgs e) => CommitGoto();

    private async void CommitGoto()
    {
        if (_project == null || !int.TryParse(GotoBox.Text?.Trim(), out int global)) return;
        if (_project.VideoIndexOf(global) < 0)
        {
            StatusText.Text = $"Frame {global} does not exist in any video of this project.";
            return;
        }
        ShowEditorPane();
        await JumpToGlobalAsync(global);
        Focus(); // give keyboard back to transport keys
    }

    private void OnCounterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_project == null) return;
        GotoBox.Text = CurrentGlobal.ToString();
        GotoBox.Focus();
        GotoBox.SelectAll();
    }

    // ---------- Tabs ----------

    private void OnShowEditor(object? sender, RoutedEventArgs e) => ShowEditorPane();
    private void OnShowStoryboard(object? sender, RoutedEventArgs e) => ShowStoryboardPane();
    private void OnShowGameSetup(object? sender, RoutedEventArgs e) => ShowGamePane();

    private void ShowEditorPane() => ShowPane(EditorPane);
    private void ShowStoryboardPane() => ShowPane(Storyboard);

    private void ShowGamePane()
    {
        GameSetup.Refresh();
        ShowPane(GameSetup);
    }

    private void ShowPane(Control pane)
    {
        EditorPane.IsVisible = ReferenceEquals(pane, EditorPane);
        Storyboard.IsVisible = ReferenceEquals(pane, Storyboard);
        GameSetup.IsVisible = ReferenceEquals(pane, GameSetup);
        EditorTabButton.IsEnabled = !EditorPane.IsVisible;
        StoryboardTabButton.IsEnabled = !Storyboard.IsVisible;
        GameTabButton.IsEnabled = !GameSetup.IsVisible;
    }

    // ---------- Project lifecycle ----------

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog(_settings.HypseusRoot);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } result) return;

        CloseProject();
        _project = new LdpProject
        {
            Name = result.GameFolder.Replace('_', ' '), // sensible starting internal name
            GameFolder = result.GameFolder,
            HypseusRoot = result.HypseusRoot,
        };
        _projectPath = result.ProjectPath;
        SaveProject();
        _settings.LastProjectPath = result.ProjectPath;
        _settings.HypseusRoot = result.HypseusRoot;
        _settings.Save();
        ResetUndoHistory();
        UpdateProjectUi();

        // Put the two global frameworks in place so the game can run without a
        // separate manual download.
        List<string> installed = InstallGlobalFrameworks(result.HypseusRoot);
        string fwNote = installed.Count > 0 ? $" · installed {string.Join(" + ", installed)}" : "";
        StatusText.Text = $"Project created in singe\\{result.GameFolder}{fwNote} — add your first video with '＋ Add Video…'";
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            FileTypeFilter = [new FilePickerFileType("Laserdisc Publisher project") { Patterns = ["*.ldproj"] }],
        });
        string? path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        await OpenProjectAsync(path);
    }

    private async Task OpenProjectAsync(string path)
    {
        try
        {
            LdpProject project = ProjectFile.Load(path);
            CloseProject();
            _project = project;
            _projectPath = path;
            _settings.LastProjectPath = path;
            _settings.Save();
            ResetUndoHistory();
            UpdateProjectUi();

            if (_project.Videos.Count > 0)
            {
                _suppressVideoSelection = true;
                VideoList.SelectedIndex = 0;
                _suppressVideoSelection = false;
                await ActivateVideoAsync(0);
            }
            StatusText.Text = $"Opened {Path.GetFileName(path)}";

            // Backfill thumbnails for any scenes that don't have one yet (e.g.
            // imported before thumbnail generation existed). No-op once cached.
            await GenerateMissingThumbnailsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to open project: " + ex.Message;
        }
    }

    private void CloseProject()
    {
        StopPlayback();
        DisposeAudioPlayers();
        foreach (FrameEngine engine in _engines.Values) engine.Dispose();
        _engines.Clear();
        _engine = null;
        _activeVideo = -1;
        _project = null;
        _projectPath = null;
        _dirty = false;
        _markIn = _markOut = null;
        _videoItems.Clear();
        _clipItems.Clear();
        VideoImage.Source = null;
        _bitmap = null;
        _undoStack.Clear();
        _redoStack.Clear();
        _lastSnapshot = null;
        Storyboard.SetProject(null, LookupClip);
        GameSetup.SetProject(null, () => null, () => null);
    }

    private void OnSaveProject(object? sender, RoutedEventArgs e) => SaveProject();

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _projectPath == null) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project as",
            DefaultExtension = "ldproj",
            SuggestedFileName = _project.Name + "-copy.ldproj",
            FileTypeChoices = [new FilePickerFileType("Laserdisc Publisher project") { Patterns = ["*.ldproj"] }],
        });
        string? newPath = file?.TryGetLocalPath();
        if (newPath == null || string.Equals(newPath, _projectPath, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            // Video paths are stored relative to the project file; re-anchor
            // them to the new location so the copy works from anywhere.
            string oldPath = _projectPath;
            foreach (VideoSource video in _project.Videos)
            {
                string absolute = ProjectFile.ResolveVideoPath(oldPath, video);
                video.Path = ProjectFile.RelativizeVideoPath(newPath, absolute);
            }

            // Thumbnails live in the per-project cache folder; copy them over.
            string oldCache = ProjectFile.CacheDir(oldPath);
            string newCache = ProjectFile.CacheDir(newPath);
            foreach (string source in Directory.EnumerateFiles(oldCache, "*.png"))
                File.Copy(source, Path.Combine(newCache, Path.GetFileName(source)), overwrite: true);

            _project.Name = Path.GetFileNameWithoutExtension(newPath);
            _projectPath = newPath;
            SaveProject();
            _settings.LastProjectPath = newPath;
            _settings.Save();
            ProjectNameText.Text = _project.Name;
            _lastSnapshot = ProjectFile.Serialize(_project); // rename isn't an undo step
            StatusText.Text = $"Saved as {Path.GetFileName(newPath)} — now working in the copy";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save As failed: " + ex.Message;
        }
    }

    private void SaveProject(bool auto = false)
    {
        if (_project == null || _projectPath == null) return;
        try
        {
            ProjectFile.Save(_project, _projectPath);
            _dirty = false;
            AutosaveText.Text = (auto ? "autosaved " : "saved ") + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            AutosaveText.Text = "SAVE FAILED: " + ex.Message;
        }
    }

    private void MarkDirty()
    {
        if (_project != null && _lastSnapshot != null)
        {
            string now = ProjectFile.Serialize(_project);
            if (now != _lastSnapshot)
            {
                _undoStack.Push(_lastSnapshot);
                _redoStack.Clear();
                _lastSnapshot = now;
            }
        }
        _dirty = true;
        AutosaveText.Text = "unsaved changes";
    }

    // ---------- Undo / redo ----------

    private void ResetUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _lastSnapshot = _project != null ? ProjectFile.Serialize(_project) : null;
    }

    private async void Undo()
    {
        if (_project == null || _undoStack.Count == 0) { StatusText.Text = "Nothing to undo."; return; }
        _redoStack.Push(ProjectFile.Serialize(_project));
        await RestoreSnapshotAsync(_undoStack.Pop());
        StatusText.Text = $"Undone ({_undoStack.Count} more in history)";
    }

    private async void Redo()
    {
        if (_project == null || _redoStack.Count == 0) { StatusText.Text = "Nothing to redo."; return; }
        _undoStack.Push(ProjectFile.Serialize(_project));
        await RestoreSnapshotAsync(_redoStack.Pop());
        StatusText.Text = "Redone";
    }

    private async Task RestoreSnapshotAsync(string snapshot)
    {
        LdpProject restored = ProjectFile.Deserialize(snapshot);
        bool videosChanged =
            _project == null ||
            _project.Videos.Count != restored.Videos.Count ||
            !_project.Videos.Select(v => v.Path).SequenceEqual(restored.Videos.Select(v => v.Path));

        StopPlayback();
        int previousVideo = _activeVideo;
        _project = restored;
        _lastSnapshot = snapshot;

        if (videosChanged)
        {
            DisposeAudioPlayers();
            foreach (FrameEngine engine in _engines.Values) engine.Dispose();
            _engines.Clear();
            _engine = null;
            _activeVideo = -1;
        }

        UpdateProjectUi();
        SaveProject();

        if (_project.Videos.Count > 0)
        {
            int target = Math.Clamp(previousVideo < 0 ? 0 : previousVideo, 0, _project.Videos.Count - 1);
            _suppressVideoSelection = true;
            VideoList.SelectedIndex = target;
            _suppressVideoSelection = false;
            _activeVideo = -1; // force reactivation so slider/counter rebind
            await ActivateVideoAsync(target);
        }
    }

    private void UpdateProjectUi()
    {
        bool hasProject = _project != null;
        ProjectNameText.Text = _project?.Name ?? "";
        SaveProjectButton.IsEnabled = hasProject;
        SaveAsButton.IsEnabled = hasProject;
        ImportSingeButton.IsEnabled = hasProject;
        ExportSingeButton.IsEnabled = hasProject;
        TestHypseusButton.IsEnabled = hasProject && !string.IsNullOrWhiteSpace(_project!.HypseusRoot);
        AddVideoButton.IsEnabled = hasProject;
        RemoveVideoButton.IsEnabled = false; // re-enabled when a video is selected
        MarkInButton.IsEnabled = MarkOutButton.IsEnabled = hasProject;

        _videoItems.Clear();
        _clipItems.Clear();
        if (_project == null) return;

        for (int i = 0; i < _project.Videos.Count; i++)
            _videoItems.Add(new VideoItem { Index = i, Source = _project.Videos[i] });

        foreach (Clip clip in _project.Clips)
            _clipItems.Add(MakeClipItem(clip));

        Storyboard.SetProject(_project, LookupClip);
        GameSetup.SetProject(_project,
            () => SelectedClip,
            () => _project != null && _activeVideo >= 0 ? CurrentGlobal : null,
            ComputeTemplateDefaults());
    }

    /// <summary>Template defaults (scoring etc.) shown as placeholders in Game Setup.</summary>
    private IReadOnlyDictionary<string, string> ComputeTemplateDefaults()
    {
        try
        {
            string text = FindSingeTemplate() is { } p ? File.ReadAllText(p) : SingeTemplate.DefaultTemplate;
            return SingeTemplate.ExtractDefaults(text);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }

    private ClipItem MakeClipItem(Clip clip)
    {
        var item = new ClipItem { Clip = clip };
        if (_projectPath != null)
        {
            string thumbPath = Path.Combine(ProjectFile.CacheDir(_projectPath), clip.Id + ".png");
            if (File.Exists(thumbPath))
            {
                try { item.Thumbnail = new Bitmap(thumbPath); }
                catch (Exception) { }
            }
        }
        return item;
    }

    // ---------- Singe import / export ----------

    private async void OnImportSinge(object? sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Singe game script",
            FileTypeFilter =
            [
                new FilePickerFileType("Singe scripts") { Patterns = ["*.singe", "*.lua"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        string? path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;

        try
        {
            string script = await File.ReadAllTextAsync(path);
            SingeImporter.Result result = SingeImporter.Import(_project, script);
            MarkDirty();
            SaveProject();
            UpdateProjectUi();
            StatusText.Text = $"Imported {Path.GetFileName(path)}: {result.Levels} levels, {result.Scenes} scenes, " +
                              $"{result.Moves} moves, {result.Deaths} deaths, {result.SlotsFilled} slots" +
                              (result.Warnings.Count > 0 ? $" — ⚠ {result.Warnings.Count} warnings (see project notes)" : "");
            if (result.Warnings.Count > 0)
                StatusText.Text += " " + string.Join(" · ", result.Warnings.Take(2));
            ShowStoryboardPane();

            // Give every imported scene a thumbnail from the loaded videos.
            await GenerateMissingThumbnailsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
        }
    }

    /// <summary>The .singe / .txt base name and directory (the game folder next to the .ldproj).</summary>
    private (string ScriptPath, string FramePath)? GameFilePaths()
    {
        if (_project == null || _projectPath == null) return null;
        string dir = Path.GetDirectoryName(_projectPath)!;
        string baseName = _project.EffectiveGameFolder;
        return (Path.Combine(dir, baseName + ".singe"), Path.Combine(dir, baseName + ".txt"));
    }

    /// <summary>Fills the template and writes the .singe + frame file into the game folder.</summary>
    private async Task<List<string>?> ExportGameAsync()
    {
        if (_project == null || GameFilePaths() is not { } paths) return null;

        string templateText = FindSingeTemplate() is { } templatePath
            ? await File.ReadAllTextAsync(templatePath)
            : SingeTemplate.DefaultTemplate;

        SingeTemplate.Result filled = SingeTemplate.Apply(_project, templateText);
        await File.WriteAllTextAsync(paths.ScriptPath, filled.Script);
        await File.WriteAllTextAsync(paths.FramePath, SingeExporter.BuildFrameFile(_project));
        return filled.Warnings;
    }

    private async void OnExportSinge(object? sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        if (GameFilePaths() is not { } paths)
        {
            StatusText.Text = "Save the project first so the game folder is known.";
            return;
        }

        try
        {
            List<string>? warnings = await ExportGameAsync();
            if (warnings == null) return;
            StatusText.Text = $"Exported {Path.GetFileName(paths.ScriptPath)} + frame file to the game folder" +
                              (warnings.Count > 0
                                  ? $" — ⚠ {warnings.Count} warnings: {string.Join(" · ", warnings.Take(3))}"
                                  : " — no warnings");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
    }

    private async void OnTestInHypseus(object? sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        if (string.IsNullOrWhiteSpace(_project.HypseusRoot) || !File.Exists(Path.Combine(_project.HypseusRoot, "hypseus.exe")))
        {
            StatusText.Text = "This project has no Hypseus folder recorded. Create it via New Project, " +
                              "or place the project under your Hypseus singe\\ folder.";
            return;
        }
        try
        {
            List<string>? warnings = await ExportGameAsync();
            if (warnings == null) return;

            // The chosen framework's globals.singe must actually be present, or
            // Hypseus will fail on launch. Global frameworks live in the parent
            // singe/ (auto-install them if missing); a standalone Structure
            // lives inside the game folder and is the author's to supply.
            if (!_project.Framework.IsStandalone())
                InstallGlobalFrameworks(_project.HypseusRoot);
            if (FrameworkGlobalsMissing() is { } missing)
                warnings.Insert(0, missing);

            var dialog = new TestHypseusDialog(_project.HypseusRoot, _project.EffectiveGameFolder, warnings);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not prepare test: " + ex.Message;
        }
    }

    /// <summary>Installs the bundled global frameworks into the Hypseus singe/ folder (only what's missing).</summary>
    private List<string> InstallGlobalFrameworks(string hypseusRoot)
    {
        try
        {
            return FrameworkInstaller.EnsureInstalled(hypseusRoot);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not install global frameworks: " + ex.Message;
            return [];
        }
    }

    /// <summary>Returns a warning if the chosen framework's globals.singe isn't on disk, else null.</summary>
    private string? FrameworkGlobalsMissing()
    {
        if (_project == null || _projectPath == null) return null;
        string dir = SingeGen.FrameworkDir(_project.Framework);
        string globals = _project.Framework.IsStandalone()
            ? Path.Combine(Path.GetDirectoryName(_projectPath)!, dir, "globals.singe")   // inside the game folder
            : Path.Combine(_project.HypseusRoot, "singe", dir, "globals.singe");         // parent singe/
        if (File.Exists(globals)) return null;

        return _project.Framework.IsStandalone()
            ? $"The '{dir}' framework folder is missing from the game folder. A standalone " +
              $"'{dir}' framework must be supplied inside the game folder before the game will run."
            : $"The global '{dir}' framework was not found at singe\\{dir}\\. Make sure that " +
              "framework is installed in your Hypseus folder.";
    }

    /// <summary>
    /// Locates the .singe template to fill: one next to the project file wins
    /// (template.singe), then a per-framework app template, then the app's
    /// default template (the dofile line adapts to the chosen framework).
    /// </summary>
    private string? FindSingeTemplate()
    {
        if (_project == null || _projectPath == null) return null;

        string projectLocal = Path.Combine(Path.GetDirectoryName(_projectPath)!, "template.singe");
        if (File.Exists(projectLocal)) return projectLocal;

        string perFramework = Path.Combine(AppContext.BaseDirectory, "Templates",
                                           $"{_project.Framework}.template.singe");
        if (File.Exists(perFramework)) return perFramework;

        string fallback = Path.Combine(AppContext.BaseDirectory, "Templates", "default.template.singe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>
    /// Decodes and caches a thumbnail for every scene that doesn't have one,
    /// resolving each scene's video from its global start frame. Runs after
    /// import (and can be re-run any time), and only touches scenes whose
    /// frames fall inside a loaded video.
    /// </summary>
    private async Task GenerateMissingThumbnailsAsync()
    {
        if (_project == null || _projectPath == null) return;
        string cacheDir = ProjectFile.CacheDir(_projectPath);

        List<Clip> todo = _project.Clips
            .Where(c => !File.Exists(Path.Combine(cacheDir, c.Id + ".png")))
            .Where(c => _project.VideoIndexOf(c.StartFrame) >= 0)
            .OrderBy(c => c.StartFrame) // group by video for warm sequential decodes
            .ToList();
        if (todo.Count == 0) return;

        ScanOverlay.IsVisible = true;
        int done = 0, made = 0;
        await _decodeGate.WaitAsync();
        try
        {
            foreach (Clip clip in todo)
            {
                int videoIndex = _project.VideoIndexOf(clip.StartFrame);
                if (videoIndex < 0) continue;
                // Opening a not-yet-loaded video toggles the overlay off in its
                // own finally, so (re)assert it after, then update the caption.
                FrameEngine? engine = await GetEngineAsync(videoIndex);
                if (engine == null) continue;
                ScanOverlay.IsVisible = true;
                ScanText.Text = $"Generating scene thumbnails… {++done}/{todo.Count}";
                ScanProgress.Value = (double)done / todo.Count;

                int local = clip.StartFrame - _project.Videos[videoIndex].GlobalBase;
                try
                {
                    FrameImage image = await Task.Run(() => engine.GetFrame(local));
                    WriteableBitmap thumb = Thumbnails.FromFrame(image);
                    thumb.Save(Path.Combine(cacheDir, clip.Id + ".png"));
                    made++;
                }
                catch (Exception) { /* skip scenes that fail to decode */ }
            }
        }
        finally
        {
            _decodeGate.Release();
            ScanOverlay.IsVisible = false;
        }

        // Reload items so the new thumbnails show in the bin and on the canvas.
        if (made > 0)
        {
            int keepActive = _activeVideo;
            UpdateProjectUi();
            if (keepActive >= 0 && keepActive < _videoItems.Count)
            {
                _suppressVideoSelection = true;
                VideoList.SelectedIndex = keepActive;
                _suppressVideoSelection = false;
            }
            StatusText.Text = $"Generated {made} scene thumbnail(s).";
        }
    }

    // ---------- Videos ----------

    private async void OnAddVideo(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _projectPath == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add video (already .m2v, or a source to convert)",
            FileTypeFilter =
            [
                new FilePickerFileType("Video (m2v, mkv, mp4, webm)")
                    { Patterns = ["*.m2v", "*.mpv", "*.mkv", "*.mp4", "*.webm", "*.mov", "*.m4v", "*.avi", "*.ts"] },
                new FilePickerFileType("MPEG-2 ready (*.m2v)") { Patterns = ["*.m2v", "*.mpv"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        string? path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;

        // A non-.m2v source (mkv/mp4/webm) can't be played by Hypseus — route it
        // through conversion first, then add the resulting .m2v.
        if (IsM2vStream(path))
            await TryAddVideoAsync(path);
        else
            await ConvertThenAddAsync([path]);
    }

    private static bool IsM2vStream(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".m2v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mpv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens (indexing if needed) a .m2v and adds it as a project video,
    /// enforcing the one-frame-rate rule. Returns true on success.</summary>
    private async Task<bool> TryAddVideoAsync(string path)
    {
        if (_project == null || _projectPath == null) return false;
        try
        {
            FrameEngine engine = await OpenEngineAsync(path);

            // Every video in a game must share one frame rate — the whole move
            // timing is frames, so a mismatch silently breaks the timing.
            if (_project.Videos.Count > 0)
            {
                double existing = _project.Videos[0].Fps;
                if (Math.Abs(existing - engine.Fps) > 0.01)
                {
                    engine.Dispose();
                    StatusText.Text = $"Can't add {Path.GetFileName(path)}: it is {engine.Fps:F3} fps but the " +
                                      $"project's videos are {existing:F3} fps. All videos must share one frame rate.";
                    return false;
                }
            }

            var source = new VideoSource
            {
                Path = ProjectFile.RelativizeVideoPath(_projectPath, path),
                FileLength = engine.Index.FileLength,
                PictureCount = engine.Index.CodedPictureCount,
                GlobalBase = _project.NextGlobalBase(),
                Fps = engine.Fps,
                Width = engine.Index.Width,
                Height = engine.Index.Height,
            };
            _project.Videos.Add(source);
            int index = _project.Videos.Count - 1;
            _engines[index] = engine;
            _videoItems.Add(new VideoItem { Index = index, Source = source });
            MarkDirty();
            SaveProject();
            _suppressVideoSelection = true;
            VideoList.SelectedIndex = index;
            _suppressVideoSelection = false;
            await ActivateVideoAsync(index);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to add video: " + ex.Message;
            return false;
        }
    }

    private async void OnConvertVideo(object? sender, RoutedEventArgs e) => await ConvertThenAddAsync(null);

    /// <summary>Opens the conversion dialog (optionally seeded with source files) and,
    /// if the user asked, adds the produced .m2v files to the project.</summary>
    private async Task ConvertThenAddAsync(IReadOnlyList<string>? seedFiles)
    {
        bool projectOpen = _project != null && _projectPath != null;
        var dialog = new ConvertVideoDialog(_settings, projectOpen, seedFiles);
        await dialog.ShowDialog(this);

        if (dialog.ProducedM2v.Count == 0) return;

        if (!projectOpen || !dialog.AddToProject)
        {
            StatusText.Text = $"Converted {dialog.ProducedM2v.Count} video(s) to .m2v" +
                              (projectOpen ? " (not added to the project)." : ".");
            return;
        }

        int added = 0;
        foreach (string m2v in dialog.ProducedM2v)
            if (File.Exists(m2v) && await TryAddVideoAsync(m2v)) added++;

        int skipped = dialog.ProducedM2v.Count - added;
        StatusText.Text = added > 0
            ? $"Converted and added {added} video(s)" + (skipped > 0 ? $" — {skipped} skipped (see log)." : ".")
            : "Converted, but no videos were added (see log for why).";
    }

    private async void OnVideoSelected(object? sender, SelectionChangedEventArgs e)
    {
        RemoveVideoButton.IsEnabled = _project != null && VideoList.SelectedIndex >= 0;
        if (_suppressVideoSelection || _project == null ||
            VideoList.SelectedIndex < 0 || VideoList.SelectedIndex == _activeVideo) return;
        await ActivateVideoAsync(VideoList.SelectedIndex);
    }

    private async void OnRemoveVideo(object? sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        int index = VideoList.SelectedIndex;
        if (index < 0 || index >= _project.Videos.Count) return;

        VideoSource removed = _project.Videos[index];
        int lastFrame = removed.GlobalBase + removed.PictureCount - 1;

        // Scenes anchored inside this video's range would be orphaned. Report
        // the count, but proceed — the whole removal is a single Ctrl+Z away.
        int orphaned = _project.Clips.Count(c =>
            c.StartFrame >= removed.GlobalBase && c.StartFrame <= lastFrame);

        // Remaining videos keep their global bases, so every other scene's
        // frame references stay valid (a gap is left where this video was).
        _project.Videos.RemoveAt(index);

        // Video indices shift, so the index-keyed engine/audio caches are stale.
        DisposeAudioPlayers();
        foreach (FrameEngine engine in _engines.Values) engine.Dispose();
        _engines.Clear();
        _engine = null;
        _activeVideo = -1;

        MarkDirty();
        SaveProject();
        UpdateProjectUi();

        if (_project.Videos.Count > 0)
        {
            int target = Math.Clamp(index, 0, _project.Videos.Count - 1);
            _suppressVideoSelection = true;
            VideoList.SelectedIndex = target;
            _suppressVideoSelection = false;
            await ActivateVideoAsync(target);
        }
        else
        {
            VideoImage.Source = null;
            _bitmap = null;
            FrameSlider.IsEnabled = false;
            FileInfoText.Text = "No videos — add one with '＋ Add Video…'";
        }

        StatusText.Text = $"Removed {Path.GetFileName(removed.Path)}" +
                          (orphaned > 0
                              ? $" — ⚠ {orphaned} scene(s) now reference no video (Ctrl+Z to undo)"
                              : " (Ctrl+Z to undo)");
    }

    private async Task<FrameEngine> OpenEngineAsync(string path)
    {
        ScanOverlay.IsVisible = true;
        ScanText.Text = $"Indexing {Path.GetFileName(path)}…";
        ScanProgress.Value = 0;
        try
        {
            var progress = new Progress<double>(p => ScanProgress.Value = p);
            return await Task.Run(() => FrameEngine.Open(path, progress));
        }
        finally
        {
            ScanOverlay.IsVisible = false;
        }
    }

    /// <summary>Returns the (cached or freshly opened) engine for a video, or null on failure.</summary>
    private async Task<FrameEngine?> GetEngineAsync(int index)
    {
        if (_project == null || _projectPath == null) return null;
        if (_engines.TryGetValue(index, out FrameEngine? engine)) return engine;

        string path = ProjectFile.ResolveVideoPath(_projectPath, _project.Videos[index]);
        try
        {
            engine = await OpenEngineAsync(path);
            _engines[index] = engine;
            return engine;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cannot open {path}: {ex.Message}";
            return null;
        }
    }

    private async Task ActivateVideoAsync(int index, bool keepQueue = false)
    {
        if (_project == null || _projectPath == null) return;
        StopPlayback(clearQueue: !keepQueue);

        FrameEngine? engine = await GetEngineAsync(index);
        if (engine == null) return;

        _engine = engine;
        _activeVideo = index;
        _markIn = _markOut = null;
        UpdateMarkUi();

        VideoSource source = _project.Videos[index];
        int lastGlobal = source.GlobalBase + source.PictureCount - 1;
        _counterDigits = Math.Max(6, lastGlobal.ToString().Length);
        FileInfoText.Text = $"{Path.GetFileName(source.Path)}  ·  {source.Width}x{source.Height}" +
                            $"  ·  {source.Fps:F3} fps  ·  {source.PictureCount} frames" +
                            $"  ·  global {source.GlobalBase}–{lastGlobal}";
        FrameTotal.Text = "/ " + lastGlobal.ToString().PadLeft(_counterDigits, '0');

        _updatingSlider = true;
        FrameSlider.Maximum = source.PictureCount - 1;
        FrameSlider.Value = 0;
        _updatingSlider = false;
        FrameSlider.IsEnabled = true;

        RepaintMarkerStrip();
        ShowFrame(0);
    }

    // ---------- Frame display ----------

    private async void ShowFrame(int localTarget)
    {
        if (_engine == null) return;
        localTarget = Math.Clamp(localTarget, 0, _engine.FrameCount - 1);
        _currentLocal = localTarget;
        UpdateCounter();

        long seq = Interlocked.Increment(ref _requestSeq);
        await _decodeGate.WaitAsync();
        try
        {
            if (seq != Interlocked.Read(ref _requestSeq)) return;
            FrameEngine engine = _engine;
            FrameImage image = await Task.Run(() => engine.GetFrame(localTarget));
            Present(image);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Decode error: " + ex.Message;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private void Present(FrameImage image)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != image.Width || _bitmap.PixelSize.Height != image.Height)
        {
            _bitmap = new WriteableBitmap(new Avalonia.PixelSize(image.Width, image.Height),
                                          new Avalonia.Vector(96, 96),
                                          PixelFormat.Bgra8888, AlphaFormat.Opaque);
            VideoImage.Source = _bitmap;
        }

        using (ILockedFramebuffer fb = _bitmap.Lock())
        {
            if (fb.RowBytes == image.Stride)
            {
                Marshal.Copy(image.Bgra, 0, fb.Address, image.Stride * image.Height);
            }
            else
            {
                for (int y = 0; y < image.Height; y++)
                    Marshal.Copy(image.Bgra, y * image.Stride, fb.Address + y * fb.RowBytes, image.Stride);
            }
        }
        VideoImage.InvalidateVisual();
    }

    private void UpdateCounter()
    {
        FrameCounter.Text = CurrentGlobal.ToString().PadLeft(_counterDigits, '0');
        if (!_updatingSlider)
        {
            _updatingSlider = true;
            FrameSlider.Value = _currentLocal;
            _updatingSlider = false;
        }
        UpdateInputOverlay();
    }

    private static readonly Avalonia.Media.IBrush OverlayActiveBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFE24C")); // bright yellow
    private static readonly Avalonia.Media.IBrush OverlaySkipBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0FFFFFF")); // translucent white

    /// <summary>
    /// During playback, shows the current input as a large glyph over the
    /// video (bright yellow, bottom-right) for exactly its window, and marks
    /// the current + next moves in the interactions list so the author can
    /// follow along.
    /// </summary>
    private void UpdateInputOverlay()
    {
        if (!_playing || _project == null)
        {
            InputOverlay.IsVisible = false;
            ClearMoveHighlights();
            return;
        }

        Clip? clip = _playQueue != null && _playQueueIndex < _playQueue.Count
            ? _playQueue[_playQueueIndex]
            : SelectedClip;
        if (clip == null || !SceneIsActive(clip))
        {
            InputOverlay.IsVisible = false;
            ClearMoveHighlights();
            return;
        }

        int global = CurrentGlobal;
        int window = _project.BaseWindowFrames;

        // Current move = playhead inside its window; next = nearest start ahead.
        InteractionMarker? active = clip.Interactions
            .Where(m => global >= m.Frame && global <= m.WindowEnd(window))
            .OrderBy(m => m.Frame).FirstOrDefault();
        InteractionMarker? upcoming = clip.Interactions
            .Where(m => m.Frame > global).OrderBy(m => m.Frame).FirstOrDefault();

        if (active != null)
        {
            InputOverlay.Text = InteractionItem.Glyph(active.Input);
            InputOverlay.Foreground = active.Input == InputKind.Skip ? OverlaySkipBrush : OverlayActiveBrush;
            InputOverlay.IsVisible = true;
        }
        else
        {
            InputOverlay.IsVisible = false;
        }

        // Follow-along highlights in the moves list (which shows this scene).
        InteractionItem? activeItem = null;
        foreach (InteractionItem item in _interactionItems)
        {
            item.Active = active != null && item.Marker.Id == active.Id;
            item.Upcoming = active == null && upcoming != null && item.Marker.Id == upcoming.Id;
            if (item.Active) activeItem = item;
        }

        // Keep the current move on screen (only scroll when it changes, so we
        // don't fight the user if they scroll the list themselves).
        Guid? cursor = active?.Id ?? upcoming?.Id;
        if (cursor != _scrolledActiveMove)
        {
            _scrolledActiveMove = cursor;
            InteractionItem? target = activeItem
                ?? (upcoming != null ? _interactionItems.FirstOrDefault(i => i.Marker.Id == upcoming.Id) : null);
            if (target != null) InteractionList.ScrollIntoView(target);
        }
    }

    private void ClearMoveHighlights()
    {
        foreach (InteractionItem item in _interactionItems)
        {
            item.Active = false;
            item.Upcoming = false;
        }
    }

    // ---------- Transport ----------

    private void OnTransport(object? sender, RoutedEventArgs e)
    {
        if (_engine == null || sender is not Button b) return;
        switch (b.Tag as string)
        {
            case "home": StopPlayback(); ShowFrame(0); break;
            case "end": StopPlayback(); ShowFrame(_engine.FrameCount - 1); break;
            case "play": TogglePlayback(); break;
            default:
                StopPlayback();
                if (int.TryParse(b.Tag as string, out int delta)) ShowFrame(_currentLocal + delta);
                break;
        }
    }

    private void TogglePlayback()
    {
        if (_engine == null) return;
        if (_playing) { StopPlayback(); return; }

        _playing = true;
        PlayButton.Content = "⏸";
        EndOverlay.IsVisible = false;
        StartAudio();
        _playTimer ??= new DispatcherTimer();
        _playTimer.Interval = TimeSpan.FromSeconds(1.0 / _engine.Fps);
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
    }

    /// <summary>Starts the companion audio at the current frame's time, if any.</summary>
    private void StartAudio()
    {
        _playingAudio?.Stop();
        _playingAudio = GetAudioPlayer(_activeVideo);
        if (_playingAudio != null && _engine != null)
            _playingAudio.PlayFrom(_currentLocal / _engine.Fps);
    }

    private AudioPlayer? GetAudioPlayer(int videoIndex)
    {
        if (_project == null || _projectPath == null || videoIndex < 0) return null;
        if (_audioPlayers.TryGetValue(videoIndex, out AudioPlayer? cached)) return cached;

        string m2vPath = ProjectFile.ResolveVideoPath(_projectPath, _project.Videos[videoIndex]);
        AudioTrack? track = AudioTrack.TryOpenFor(m2vPath);
        AudioPlayer? player = track != null ? new AudioPlayer(track) : null;
        _audioPlayers[videoIndex] = player;
        if (player == null && _warnedNoAudio.Add(videoIndex))
            StatusText.Text = $"No matching .ogg for {Path.GetFileName(m2vPath)} — silent playback.";
        return player;
    }

    private void DisposeAudioPlayers()
    {
        _playingAudio = null;
        foreach (AudioPlayer? player in _audioPlayers.Values) player?.Dispose();
        _audioPlayers.Clear();
        _warnedNoAudio.Clear();
    }

    private void StopPlayback(bool clearQueue = true)
    {
        if (clearQueue)
        {
            _playQueue = null;
            _playQueueIndex = 0;
        }
        _playStopGlobal = null;
        InputOverlay.IsVisible = false;
        ClearMoveHighlights();
        _playingAudio?.Stop();
        _playingAudio = null;

        // A full stop (not a mid-flow video switch) hides the end banner and
        // hands the interactions panel back to the bin selection.
        if (clearQueue)
        {
            EndOverlay.IsVisible = false;
            if (_playingClip != null)
            {
                _playingClip = null;
                RefreshInteractions();
                RepaintMarkerStrip();
            }
        }

        if (!_playing) return;
        _playing = false;
        PlayButton.Content = "▶";
        if (_playTimer != null)
        {
            _playTimer.Stop();
            _playTimer.Tick -= OnPlayTick;
        }
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_engine == null) { StopPlayback(); return; }

        bool atVideoEnd = _currentLocal >= _engine.FrameCount - 1;
        bool atStopFrame = _playStopGlobal is { } stop && CurrentGlobal >= stop;
        if (atVideoEnd || atStopFrame)
        {
            if (_playQueue != null && _playQueueIndex + 1 < _playQueue.Count)
            {
                _ = AdvanceQueueAsync();
            }
            else
            {
                // Natural end of a Play Scene / Play Flow: stop on the last
                // frame and say so (a paused frame is otherwise ambiguous).
                bool single = _playIsSingleScene;
                StopPlayback();
                EndOverlayText.Text = single ? "End of Scene" : "End of Flow";
                EndOverlay.IsVisible = true;
            }
            return;
        }

        // With audio the sound device is the clock; the video follows it so
        // the two can never drift. Without audio, step one frame per tick.
        int next = _currentLocal + 1;
        if (_playingAudio != null)
        {
            int audioFrame = (int)Math.Round(_playingAudio.PositionSeconds * _engine.Fps);
            if (audioFrame <= _currentLocal) return; // sound hasn't reached the next frame yet
            next = audioFrame;
        }
        next = Math.Min(next, _engine.FrameCount - 1);
        if (_playStopGlobal is { } stopG && _project != null)
            next = Math.Min(next, stopG - _project.Videos[_activeVideo].GlobalBase);
        ShowFrame(next);
    }

    // ---------- Chained clip playback (storyboard flow) ----------

    private async void PlayClipSequence(IReadOnlyList<Clip> clips)
    {
        if (_project == null || clips.Count == 0) return;
        StopPlayback();
        _playQueue = clips.ToList();
        _playQueueIndex = -1;
        _playIsSingleScene = clips.Count == 1;
        EndOverlay.IsVisible = false;
        await AdvanceQueueAsync();
    }

    private async Task AdvanceQueueAsync()
    {
        if (_playQueue == null) return;
        _playTimer?.Stop();
        _playQueueIndex++;
        Clip clip = _playQueue[_playQueueIndex];
        _playingClip = clip;
        StatusText.Text = $"Playing {_playQueueIndex + 1}/{_playQueue.Count}: {clip.Name}";

        // Visual echo in the bin (the panel itself follows _playingClip).
        if (_clipItems.FirstOrDefault(c => c.Clip.Id == clip.Id) is { } item && ClipList.SelectedItem != item)
            ClipList.SelectedItem = item;

        await JumpCoreAsync(clip.StartFrame, keepQueue: true);

        // After the jump, _activeVideo is correct, so the panel and marker
        // strip reflect the scene that's now on screen.
        RefreshInteractions();
        RepaintMarkerStrip();
        _playStopGlobal = clip.EndFrame;
        if (_playing)
        {
            StartAudio(); // re-anchor sound to the new scene's start
            _playTimer!.Start();
        }
        else
        {
            TogglePlayback();
        }
    }

    private async Task JumpCoreAsync(int globalFrame, bool keepQueue)
    {
        if (_project == null) return;

        // Which video holds this frame is derived from the global frame, never
        // from a stored index — this is what keeps scenes pointing at the right
        // video no matter how they were created (marked, imported, or slotted).
        int videoIndex = _project.VideoIndexOf(globalFrame);
        if (videoIndex < 0)
        {
            StatusText.Text = $"Frame {globalFrame} isn't inside any loaded video " +
                              "(is the video for this scene added, and in the right order?).";
            return;
        }

        if (videoIndex != _activeVideo)
        {
            _suppressVideoSelection = true;
            VideoList.SelectedIndex = videoIndex;
            _suppressVideoSelection = false;
            await ActivateVideoAsync(videoIndex, keepQueue);
        }
        ShowFrame(globalFrame - _project.Videos[videoIndex].GlobalBase);
    }

    private async Task JumpToGlobalAsync(int globalFrame)
    {
        StopPlayback();
        await JumpCoreAsync(globalFrame, keepQueue: false);
    }

    private void OnSliderChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingSlider || _engine == null) return;
        StopPlayback();
        ShowFrame((int)FrameSlider.Value);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return; // text inputs own their keys

        // Undo/redo apply everywhere (editor and storyboard alike).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { Redo(); e.Handled = true; return; }
            if (e.Key == Key.Z) { Undo(); e.Handled = true; return; }
            if (e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        }

        if (Storyboard.IsVisible)
        {
            Storyboard.HandleKey(e);
            return;
        }

        if (ClipForm.IsVisible && e.Key == Key.Escape)
        {
            OnClipFormCancel(null, null!);
            e.Handled = true;
            return;
        }
        if (_engine == null) return;

        int step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 100
                 : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10
                 : 1;
        switch (e.Key)
        {
            case Key.Left: StopPlayback(); ShowFrame(_currentLocal - step); e.Handled = true; break;
            case Key.Right: StopPlayback(); ShowFrame(_currentLocal + step); e.Handled = true; break;
            case Key.Home: StopPlayback(); ShowFrame(0); e.Handled = true; break;
            case Key.End: StopPlayback(); ShowFrame(_engine.FrameCount - 1); e.Handled = true; break;
            case Key.Space: TogglePlayback(); e.Handled = true; break;
            case Key.I: OnMarkIn(null, null!); e.Handled = true; break;
            case Key.O: OnMarkOut(null, null!); e.Handled = true; break;
            case Key.U: AddInteraction(InputKind.Up); e.Handled = true; break;
            case Key.D: AddInteraction(InputKind.Down); e.Handled = true; break;
            case Key.L: AddInteraction(InputKind.Left); e.Handled = true; break;
            case Key.R: AddInteraction(InputKind.Right); e.Handled = true; break;
            case Key.D1 or Key.NumPad1: AddInteraction(InputKind.Button1); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: AddInteraction(InputKind.Button2); e.Handled = true; break;
            case Key.S: AddInteraction(InputKind.Skip); e.Handled = true; break;
            case Key.E: OnSetSkipEnd(null, null!); e.Handled = true; break;
            case Key.G: GotoBox.Focus(); GotoBox.SelectAll(); e.Handled = true; break;
            case Key.Enter:
                if (NewClipButton.IsEnabled && !ClipForm.IsVisible) { OnNewClip(null, null!); e.Handled = true; }
                break;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_engine == null) return;
        StopPlayback();
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        ShowFrame(_currentLocal + (e.Delta.Y < 0 ? step : -step));
        e.Handled = true;
    }

    // ---------- Marks & clips ----------

    private void OnMarkIn(object? sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _markIn = CurrentGlobal;
        if (_markOut < _markIn) _markOut = null;
        UpdateMarkUi();
    }

    private void OnMarkOut(object? sender, RoutedEventArgs e)
    {
        if (_engine == null) return;
        _markOut = CurrentGlobal;
        if (_markIn > _markOut) _markIn = null;
        UpdateMarkUi();
    }

    private void UpdateMarkUi()
    {
        string inText = _markIn?.ToString().PadLeft(_counterDigits, '0') ?? "······";
        string outText = _markOut?.ToString().PadLeft(_counterDigits, '0') ?? "······";
        MarkText.Text = _markIn == null && _markOut == null
            ? ""
            : $"⟦ {inText} → {outText} ⟧" +
              (_markIn != null && _markOut != null ? $" ({_markOut - _markIn + 1} fr)" : "");
        NewClipButton.IsEnabled = _markIn != null && _markOut != null;
    }

    private void OnNewClip(object? sender, RoutedEventArgs e)
    {
        if (_markIn is not { } markIn || _markOut is not { } markOut) return;
        ClipFormRange.Text = $"Frames {markIn.ToString().PadLeft(_counterDigits, '0')} → " +
                             $"{markOut.ToString().PadLeft(_counterDigits, '0')} ({markOut - markIn + 1} frames)";
        ClipNameBox.Text = "";
        ClipDescBox.Text = "";
        ClipForm.IsVisible = true;
        ClipNameBox.Focus();
    }

    private void OnClipFormCancel(object? sender, RoutedEventArgs e) => ClipForm.IsVisible = false;

    private void OnClipFormKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter accepts from either text box (the description box uses
        // Shift+Enter for new lines); Escape cancels.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            OnClipFormCreate(null, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClipForm.IsVisible = false;
            e.Handled = true;
        }
    }

    private async void OnClipFormCreate(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _projectPath == null || _engine == null ||
            _markIn is not { } markIn || _markOut is not { } markOut) return;

        var clip = new Clip
        {
            Name = string.IsNullOrWhiteSpace(ClipNameBox.Text) ? $"Scene {_project.Clips.Count + 1}" : ClipNameBox.Text.Trim(),
            Description = ClipDescBox.Text ?? "",
            StartFrame = markIn,
            EndFrame = markOut,
        };
        _project.Clips.Add(clip);

        ClipItem item = new() { Clip = clip };
        try
        {
            FrameEngine engine = _engine;
            int local = markIn - _project.Videos[_activeVideo].GlobalBase;
            FrameImage image = await Task.Run(() => engine.GetFrame(local));
            WriteableBitmap thumb = Thumbnails.FromFrame(image);
            string thumbPath = Path.Combine(ProjectFile.CacheDir(_projectPath), clip.Id + ".png");
            thumb.Save(thumbPath);
            item.Thumbnail = thumb;
        }
        catch (Exception) { /* clip without thumbnail is still a clip */ }

        _clipItems.Add(item);
        ClipForm.IsVisible = false;
        _markIn = _markOut = null;
        UpdateMarkUi();
        MarkDirty();
        SaveProject();
        StatusText.Text = $"Clip '{clip.Name}' added ({clip.FrameCount} frames)";
    }

    private void OnClipSelected(object? sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = ClipList.SelectedItem is ClipItem;
        PlayClipButton.IsEnabled = GotoClipButton.IsEnabled =
            DeleteClipButton.IsEnabled = ToStoryboardButton.IsEnabled =
            MoveClipUpButton.IsEnabled = MoveClipDownButton.IsEnabled = hasSelection;
        RefreshInteractions();
        RepaintMarkerStrip();
    }

    private void OnMoveClipUp(object? sender, RoutedEventArgs e) => MoveClip(-1);
    private void OnMoveClipDown(object? sender, RoutedEventArgs e) => MoveClip(+1);

    private void MoveClip(int direction)
    {
        if (_project == null || ClipList.SelectedItem is not ClipItem item) return;
        int index = _clipItems.IndexOf(item);
        int target = index + direction;
        if (target < 0 || target >= _clipItems.Count) return;

        _clipItems.Move(index, target);
        int modelIndex = _project.Clips.IndexOf(item.Clip);
        _project.Clips.RemoveAt(modelIndex);
        _project.Clips.Insert(modelIndex + direction, item.Clip);
        ClipList.SelectedItem = item;
        MarkDirty();
        SaveProject();
    }

    // ---------- Interactions ----------

    private void OnAddInteraction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && Enum.TryParse((string?)b.Tag, out InputKind kind))
            AddInteraction(kind);
    }

    private void AddInteraction(InputKind kind)
    {
        if (_project == null) return;
        if (SelectedClip is not { } clip)
        {
            StatusText.Text = "Select a scene in the bin first — moves belong to a scene.";
            return;
        }
        int frame = CurrentGlobal;
        if (!SceneIsActive(clip) || frame < clip.StartFrame || frame > clip.EndFrame)
        {
            StatusText.Text = $"Frame {frame} is outside '{clip.Name}' " +
                              $"({clip.StartFrame}–{clip.EndFrame}) — jog inside the scene to add moves.";
            return;
        }

        var marker = new InteractionMarker { Frame = frame, Input = kind };
        if (kind == InputKind.Skip)
        {
            // Skip windows are custom-length: default to just before the next
            // move's cushion, or the scene's end when nothing follows.
            InteractionMarker? next = clip.Interactions
                .Where(m => m.Frame > frame)
                .OrderBy(m => m.Frame)
                .FirstOrDefault();
            int end = next != null
                ? next.Frame - InteractionRules.MinSpacing(_project.BaseWindowFrames)
                : clip.EndFrame;
            marker.EndFrameOverride = Math.Max(frame, Math.Min(end, clip.EndFrame));
        }

        clip.Interactions.Add(marker);
        clip.Interactions.Sort((a, b2) => a.Frame.CompareTo(b2.Frame));
        AfterInteractionChange(clip, $"{InteractionItem.Glyph(kind)} {kind} at {frame}" +
            (marker.EndFrameOverride is { } e2 ? $"–{e2}" : ""));
    }

    private void OnDeleteInteraction(object? sender, RoutedEventArgs e)
    {
        if (SelectedClip is not { } clip || InteractionList.SelectedItem is not InteractionItem item) return;
        clip.Interactions.RemoveAll(m => m.Id == item.Marker.Id);
        AfterInteractionChange(clip, "interaction removed");
    }

    private void AfterInteractionChange(Clip clip, string what)
    {
        RefreshInteractions();
        RepaintMarkerStrip();
        Storyboard.Refresh();
        MarkDirty();
        SaveProject();

        int warnings = _interactionItems.Count(i => i.Warn);
        StatusText.Text = warnings > 0
            ? $"{what} — ⚠ {warnings} marker(s) break the spacing/window rules (min gap " +
              $"{InteractionRules.MinSpacing(_project!.BaseWindowFrames)} frames at Easy)"
            : $"{what} — spacing OK on all difficulties";
    }

    private void OnInteractionSelected(object? sender, SelectionChangedEventArgs e)
    {
        DeleteInteractionButton.IsEnabled = InteractionList.SelectedItem is InteractionItem;
        SetSkipEndButton.IsEnabled = InteractionList.SelectedItem is InteractionItem { Marker.Input: InputKind.Skip };
    }

    private void OnSetSkipEnd(object? sender, RoutedEventArgs e)
    {
        if (_project == null || SelectedClip is not { } clip ||
            InteractionList.SelectedItem is not InteractionItem item) return;
        if (item.Marker.Input != InputKind.Skip)
        {
            StatusText.Text = "Only skip moves have an editable end frame — select a skip in the list.";
            return;
        }

        int frame = CurrentGlobal;
        if (!SceneIsActive(clip) || frame <= item.Marker.Frame || frame > clip.EndFrame)
        {
            StatusText.Text = $"Skip end must be after the skip's start ({item.Marker.Frame}) " +
                              $"and within the scene (≤ {clip.EndFrame}). Jog to the frame you want, then press E.";
            return;
        }

        InteractionMarker? marker = clip.Interactions.Find(m => m.Id == item.Marker.Id);
        if (marker == null) return;
        marker.EndFrameOverride = frame;
        AfterInteractionChange(clip, $"skip end set to {frame}");
    }

    private async void OnGotoInteraction(object? sender, RoutedEventArgs e)
    {
        if (SelectedClip is null || InteractionList.SelectedItem is not InteractionItem item) return;
        ShowEditorPane();
        await JumpToGlobalAsync(item.Marker.Frame);
    }

    /// <summary>The scene whose moves the panel shows: the playing one wins over the bin selection.</summary>
    private Clip? PanelClip => _playingClip ?? SelectedClip;

    private void RefreshInteractions()
    {
        Guid? selectedId = (InteractionList.SelectedItem as InteractionItem)?.Marker.Id;
        _interactionItems.Clear();
        DeleteInteractionButton.IsEnabled = false;
        SetSkipEndButton.IsEnabled = false;
        _scrolledActiveMove = null;
        if (_project == null || PanelClip is not { } clip)
        {
            InteractionsTitle.Text = "INTERACTIONS";
            return;
        }

        HashSet<Guid> violators = InteractionRules.FindViolators(clip, _project.BaseWindowFrames);
        foreach (InteractionMarker marker in clip.Interactions.OrderBy(m => m.Frame))
            _interactionItems.Add(new InteractionItem { Marker = marker, Warn = violators.Contains(marker.Id) });

        if (selectedId is { } id)
            InteractionList.SelectedItem = _interactionItems.FirstOrDefault(i => i.Marker.Id == id);

        InteractionsTitle.Text = $"INTERACTIONS — {clip.Name} ({clip.Interactions.Count})" +
                                 (violators.Count > 0 ? $"  ⚠ {violators.Count}" : "");
    }

    private void RepaintMarkerStrip()
    {
        MarkerStrip.Children.Clear();
        if (_project == null || _activeVideo < 0) return;
        double width = MarkerStrip.Bounds.Width;
        if (width <= 1) return;

        VideoSource source = _project.Videos[_activeVideo];
        int span = Math.Max(1, source.PictureCount - 1);
        double XFor(int global) => (global - source.GlobalBase) / (double)span * width;

        if (PanelClip is { } clip && SceneIsActive(clip))
        {
            HashSet<Guid> violators = InteractionRules.FindViolators(clip, _project.BaseWindowFrames);

            var band = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = Math.Max(2, XFor(clip.EndFrame) - XFor(clip.StartFrame)),
                Height = 3,
                Fill = (Avalonia.Media.IBrush?)this.FindResource("Accent"),
                Opacity = 0.45,
            };
            Canvas.SetLeft(band, XFor(clip.StartFrame));
            Canvas.SetTop(band, 7);
            MarkerStrip.Children.Add(band);

            foreach (InteractionMarker marker in clip.Interactions)
            {
                var tick = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2,
                    Height = 10,
                    Fill = (Avalonia.Media.IBrush?)this.FindResource(
                        violators.Contains(marker.Id) ? "PortDeath" : "AccentAmber"),
                };
                Canvas.SetLeft(tick, XFor(marker.Frame) - 1);
                Canvas.SetTop(tick, 0);
                MarkerStrip.Children.Add(tick);
            }
        }
    }

    private async void OnGotoClip(object? sender, RoutedEventArgs e)
    {
        if (_project == null || ClipList.SelectedItem is not ClipItem item) return;
        ShowEditorPane();
        await JumpToGlobalAsync(item.Clip.StartFrame);
    }

    private void OnPlayClip(object? sender, RoutedEventArgs e)
    {
        if (_project == null || ClipList.SelectedItem is not ClipItem item) return;
        ShowEditorPane();
        PlayClipSequence([item.Clip]);
    }

    private void OnAddToStoryboard(object? sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipItem item) return;
        Storyboard.AddClipNode(item.Clip);
        StatusText.Text = $"'{item.Clip.Name}' added to storyboard (auto-chained; drag from the bin to place loose)";
    }

    // ---------- Drag scenes from bin to storyboard ----------

    private Point? _binDragOrigin;
    private PointerPressedEventArgs? _binDragPressArgs;

    private void OnClipListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ClipList).Properties.IsLeftButtonPressed)
        {
            _binDragOrigin = e.GetPosition(ClipList);
            _binDragPressArgs = e;
        }
    }

    private async void OnClipListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_binDragOrigin is not { } origin || _binDragPressArgs is not { } pressArgs ||
            ClipList.SelectedItem is not ClipItem item) return;
        Point pos = e.GetPosition(ClipList);
        if (Math.Abs(pos.X - origin.X) + Math.Abs(pos.Y - origin.Y) < 12) return;

        _binDragOrigin = null;
        _binDragPressArgs = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(StoryboardView.ClipDragFormat, item.Clip.Id.ToString()));
        ShowStoryboardPane();
        await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Copy);
    }

    private void OnDeleteClip(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _projectPath == null || ClipList.SelectedItem is not ClipItem item) return;
        _project.Clips.Remove(item.Clip);
        _clipItems.Remove(item);
        Storyboard.RemoveNodesForClip(item.Clip.Id);
        try { File.Delete(Path.Combine(ProjectFile.CacheDir(_projectPath), item.Clip.Id + ".png")); }
        catch (IOException) { }
        MarkDirty();
        SaveProject();
        StatusText.Text = $"Clip '{item.Clip.Name}' deleted";
    }
}
